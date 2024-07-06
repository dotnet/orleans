#nullable enable
using System;
using Orleans.Runtime;

namespace Orleans.Placement.Repartitioning;

internal interface IMessageStatisticsSink
{
    Action<Message>? GetMessageObserver();
}

internal sealed class NoOpMessageStatisticsSink : IMessageStatisticsSink
{
    public Action<Message>? GetMessageObserver() => null;
}