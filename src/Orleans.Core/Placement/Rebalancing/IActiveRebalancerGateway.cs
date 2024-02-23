using Orleans.Runtime;

namespace Orleans.Placement.Rebalancing;

internal interface IActiveRebalancerGateway
{
    void RecordMessage(Message message);
}

internal sealed class NoOpActiveRebalancerGateway : IActiveRebalancerGateway
{
    public void RecordMessage(Message message) { }
}