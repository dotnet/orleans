using Orleans.Runtime;

namespace Orleans.Placement.Repartitioning;

internal interface IMessageStatisticsSink
{
    void RecordMessage(Message message);
}

internal sealed class NoOpMessageStatisticsSink : IMessageStatisticsSink
{
    public void RecordMessage(Message message) { }
}