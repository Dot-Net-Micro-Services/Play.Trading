using System;

namespace Play.Trading.Service.Exceptions;

[Serializable]
internal class UnknownItemException : Exception
{
    private Guid ItemId;

    public UnknownItemException(Guid ItemId) : base($"Unknow Item '{ItemId}'")
    {
        this.ItemId = ItemId;
    }
}