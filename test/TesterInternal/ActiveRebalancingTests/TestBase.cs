using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Runtime.Placement.Rebalancing;
using TestExtensions;

namespace UnitTests.ActiveRebalancingTests;

public abstract class TestBase
{
    protected readonly SiloAddress _silo1;
    protected readonly SiloAddress _silo2;

    internal readonly IInternalGrainFactory _grainFactory;
    internal readonly IActiveRebalancerGrain s1_rebalancer;
    internal readonly IActiveRebalancerGrain s2_rebalancer;

    public TestBase(BaseTestClusterFixture fixture)
    {
        _grainFactory = fixture.HostedCluster.InternalGrainFactory;

        var silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s).ToArray();
        _silo1 = silos[0];
        _silo2 = silos[1];

        s1_rebalancer = IActiveRebalancerGrain.GetReference(_grainFactory, _silo1);
        s2_rebalancer = IActiveRebalancerGrain.GetReference(_grainFactory, _silo2);
    }

    public async ValueTask ResetCounters()
    {
        await s1_rebalancer.AsReference<IActiveRebalancerExtension>().ResetCounters();
        await s2_rebalancer.AsReference<IActiveRebalancerExtension>().ResetCounters();
    }
}
