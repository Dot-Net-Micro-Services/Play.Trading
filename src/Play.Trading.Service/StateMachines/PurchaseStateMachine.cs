using System;
using System.Linq;
using MassTransit;
using Microsoft.Extensions.Logging;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Service.Activities;
using Play.Trading.Service.Contracts;
using Play.Trading.Service.SignalR;

namespace Play.Trading.Service.StateMachines;

public class PurchaseStateMachine : MassTransitStateMachine<PurchaseState>
{
    private readonly MessageHub hub;
    private readonly ILogger<PurchaseStateMachine> logger;
    public State Accepted { get; }
    public State ItemsGranted { get; }
    public State Completed { get; }
    public State Faulted { get; }
    public Event<PurchaseRequested> PurchaseRequested { get; }
    public Event<GetPurchaseState> GetPurchaseState { get; }
    public Event<InventoryItemsGranted> InventoryItemsGranted { get; }
    public Event<Fault<GrantItems>> GrantItemsFaulted { get; }
    public Event<Fault<DebitGil>> DebitGilFaulted { get;}
    public Event<GilDebited> GilDebited { get; }

    public PurchaseStateMachine(MessageHub hub, ILogger<PurchaseStateMachine> logger)
    {
        InstanceState(state => state.CurrentState);
        ConfigureEvents();
        ConfigureInitialState();
        ConfigureAny();
        ConfigureAccepted();
        ConfigureItemsGranted();
        ConfigureFaulted();
        ConfigureCompleted();
        this.hub = hub;
        this.logger = logger;
    }

    private void ConfigureEvents()
    {
        Event(() => PurchaseRequested);
        Event(() => GetPurchaseState);
        Event(() => InventoryItemsGranted);
        Event(() => GilDebited);
        Event(() => GrantItemsFaulted, x => x.CorrelateById(context => context.Message.Message.CorrelationId));
        Event(() => DebitGilFaulted, x => x.CorrelateById(context => context.Message.Message.CorrelationId));
    }

    private void ConfigureInitialState()
    {
        Initially(
            When(PurchaseRequested)
                .Then(context => {
                    context.Saga.UserId = context.Message.UserId;
                    context.Saga.ItemId = context.Message.ItemId;
                    context.Saga.Quantity = context.Message.Quantity;
                    context.Saga.Received = DateTimeOffset.UtcNow;
                    context.Saga.LastUpdated = context.Saga.Received;
                    logger.LogInformation(
                        "Calculating total price for purchase with correlationId {CorrelationId}...",
                        context.Saga.CorrelationId
                    );
                })
                .Activity(x => x.OfType<CalculatePurchaseTotalActivity>())
                .Send(context => new GrantItems(
                    context.Saga.UserId,
                    context.Saga.ItemId,
                    context.Saga.Quantity,
                    context.Saga.CorrelationId
                ))
                .TransitionTo(Accepted)
                .Catch<Exception>(ex => ex
                    .Then(context => {
                        context.Saga.ErrorMessage = context.Exception.Message;
                        context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogError(
                            context.Exception,
                            "Could not Calculate the total price of purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}",
                            context.Saga.CorrelationId,
                            context.Saga.ErrorMessage
                        );
                    })
                    .TransitionTo(Faulted)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Saga))
                )
        );
    }

    private void ConfigureAccepted()
    {
        During(
            Accepted,
            Ignore(PurchaseRequested),
            When(InventoryItemsGranted)
                .Then(context => {
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogInformation(
                        "Items of purchase with CorrelationId {CorrelationId} have been granted to user {userId}.",
                        context.Saga.CorrelationId,
                        context.Saga.UserId
                    );
                })
                .Send(context => new DebitGil(
                    context.Saga.UserId,
                    context.Saga.PurchaseTotal.Value,
                    context.Saga.CorrelationId
                ))
                .TransitionTo(ItemsGranted)
                .ThenAsync(async context => await hub.SendStatusAsync(context.Saga)),
            When(GrantItemsFaulted)
                .Then(context => {
                    context.Saga.ErrorMessage = context.Message.Exceptions.First()?.Message;
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogError(
                        "Couldnot Grant Items for purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}",
                        context.Saga.CorrelationId,
                        context.Saga.ErrorMessage
                    );
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await hub.SendStatusAsync(context.Saga))
        );
    }

    private void ConfigureItemsGranted()
    {
        During(
            ItemsGranted,
            Ignore(PurchaseRequested),
            Ignore(InventoryItemsGranted),
            When(GilDebited)
                .Then(context => {
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogInformation(
                        "The total price of purchase with CorrelationId {CorrelationId} has been debited from {userId}. Purchase Compelete",
                        context.Saga.CorrelationId,
                        context.Saga.UserId
                    );
                })
                .TransitionTo(Completed)
                .ThenAsync(async context => {
                    logger.LogInformation("Purchase Order {CorrelationId} Completed", context.Saga.CorrelationId);
                    await hub.SendStatusAsync(context.Saga);
                }),
            When(DebitGilFaulted)
                .Send(context => new SubtractItems(
                    context.Saga.UserId,
                    context.Saga.ItemId,
                    context.Saga.Quantity,
                    context.Saga.CorrelationId
                ))
                .Then(context => {
                    context.Saga.ErrorMessage = context.Message.Exceptions.First().Message;
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogError(
                        "Couldnot debit the total price of purchase with CorrelationId {CorrelationId} from user {userId}. Error: {ErrorMessage}",
                        context.Saga.CorrelationId,
                        context.Saga.UserId,
                        context.Saga.ErrorMessage
                    );
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await hub.SendStatusAsync(context.Saga))
        );
    }

    private void ConfigureCompleted()
    {
        During(
            Completed,
            Ignore(PurchaseRequested),
            Ignore(InventoryItemsGranted),
            Ignore(GilDebited)
        );
    }

    private void ConfigureAny()
    {
        DuringAny(
            When(GetPurchaseState)
            .Respond(x => x.Saga)
        );
    }

    private void ConfigureFaulted()
    {
        During(
            Faulted,
            Ignore(PurchaseRequested),
            Ignore(InventoryItemsGranted),
            Ignore(GilDebited)
        );
    }
}