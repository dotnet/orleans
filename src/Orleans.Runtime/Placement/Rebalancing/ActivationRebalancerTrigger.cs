#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed class ActivationRebalancerTrigger(
    IGrainFactory grainFactory,
    IOptions<ActivationRebalancerOptions> options) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken) =>
        grainFactory.GetGrain<IActivationRebalancerGrain>(0)
            .TriggerRebalancing(options.Value.ToParameters());
}
