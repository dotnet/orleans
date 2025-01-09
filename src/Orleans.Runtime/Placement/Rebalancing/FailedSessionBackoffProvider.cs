using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed class FailedSessionBackoffProvider(IOptions<ActivationRebalancerOptions> options)
    : FixedBackoff(options.Value.SessionCyclePeriod), IFailedSessionBackoffProvider;