using System;
using System.Linq;
using Amazon.Runtime.Internal.Util;
using Automatonymous;
using Automatonymous.Activities;
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
                    context.Instance.UserId = context.Data.UserId;
                    context.Instance.ItemId = context.Data.ItemId;
                    context.Instance.Quantity = context.Data.Quantity;
                    context.Instance.Received = DateTimeOffset.UtcNow;
                    context.Instance.LastUpdated = context.Instance.Received;
                    logger.LogInformation(
                        "Calculating total price for purchase with correlationId {CorrelationId}...",
                        context.Instance.CorrelationId
                    );
                })
                .Activity(x => x.OfType<CalculatePurchaseTotalActivity>())
                .Send(context => new GrantItems(
                    context.Instance.UserId,
                    context.Instance.ItemId,
                    context.Instance.Quantity,
                    context.Instance.CorrelationId
                ))
                .TransitionTo(Accepted)
                .Catch<Exception>(ex => ex
                    .Then(context => {
                        context.Instance.ErrorMessage = context.Exception.Message;
                        context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogError(
                            context.Exception,
                            "Could not Calculate the total price of purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}",
                            context.Instance.CorrelationId,
                            context.Instance.ErrorMessage
                        );
                    })
                    .TransitionTo(Faulted)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Instance))
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
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogInformation(
                        "Items of purchase with CorrelationId {CorrelationId} have been granted to user {userId}.",
                        context.Instance.CorrelationId,
                        context.Instance.UserId
                    );
                })
                .Send(context => new DebitGil(
                    context.Instance.UserId,
                    context.Instance.PurchaseTotal.Value,
                    context.Instance.CorrelationId
                ))
                .TransitionTo(ItemsGranted),
            When(GrantItemsFaulted)
                .Then(context => {
                    context.Instance.ErrorMessage = context.Data.Exceptions.First()?.Message;
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogError(
                        "Couldnot Grant Items for purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}",
                        context.Instance.CorrelationId,
                        context.Instance.ErrorMessage
                    );
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await hub.SendStatusAsync(context.Instance))
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
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogInformation(
                        "The total price of purchase with CorrelationId {CorrelationId} has been debited from {userId}. Purchase Compelete",
                        context.Instance.CorrelationId,
                        context.Instance.UserId
                    );
                })
                .TransitionTo(Completed)
                .ThenAsync(async context => await hub.SendStatusAsync(context.Instance)),
            When(DebitGilFaulted)
                .Send(context => new SubtractItems(
                    context.Instance.UserId,
                    context.Instance.ItemId,
                    context.Instance.Quantity,
                    context.Instance.CorrelationId
                ))
                .Then(context => {
                    context.Instance.ErrorMessage = context.Data.Exceptions.First().Message;
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;
                    logger.LogError(
                        "Couldnot debit the total price of purchase with CorrelationId {CorrelationId} from user {userId}. Error: {ErrorMessage}",
                        context.Instance.CorrelationId,
                        context.Instance.UserId,
                        context.Instance.ErrorMessage
                    );
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await hub.SendStatusAsync(context.Instance))
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
            .Respond(x => x.Instance)
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