using Orleans.Runtime;

namespace Orleans.Placement.Rebalancing;

internal interface IMessageStatisticsSink
{
    void RecordMessage(Message message);
}

internal sealed class NoOpMessageStatisticsSink : IMessageStatisticsSink
{
    public void RecordMessage(Message message) { }
}