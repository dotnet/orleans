using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using Orleans.Runtime.Placement;

namespace UnitTests.ActivationRebalancingTests;

public abstract class RebalancingTestBase<TFixture> : IAsyncLifetime
    where TFixture : BaseInProcessTestClusterFixture
{
    protected InProcessTestCluster Cluster { get; }

    protected SiloAddress Silo1 { get; }
    protected SiloAddress Silo2 { get; }
    protected SiloAddress Silo3 { get; }
    protected SiloAddress Silo4 { get; }

    internal ITestOutputHelper OutputHelper { get; }
    internal IInternalGrainFactory GrainFactory { get; }
    internal IManagementGrain MgmtGrain { get; }

    /// <summary>
    /// Observer for rebalancer diagnostic events. Use this for event-driven waiting
    /// instead of Task.Delay when waiting for rebalancing cycles.
    /// </summary>
    protected RebalancerDiagnosticObserver RebalancerObserver { get; }

    protected RebalancingTestBase(TFixture fixture, ITestOutputHelper output)
    {
        var silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s).ToArray();

        Silo1 = silos[0];
        Silo2 = silos[1];
        Silo3 = silos[2];
        Silo4 = silos[3];

        Cluster = fixture.HostedCluster;
        OutputHelper = output;
        GrainFactory = (IInternalGrainFactory)fixture.HostedCluster.Client;
        MgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);
        RebalancerObserver = RebalancerDiagnosticObserver.Create();
    }

    protected static int GetActivationCount(DetailedGrainStatistic[] stats, SiloAddress silo) =>
        stats.Count(x => x.SiloAddress.Equals(silo));

    protected void AddTestActivations(List<Task> tasks, SiloAddress silo, int count)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, silo);
        for (var i = 0; i < count; i++)
        {
            tasks.Add(GrainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }
    }

    protected static int CalculateVariance(int[] values)
    {
        var mean = values.Average();
        var sumSqrtDiff = values.Select(x => (x - mean) * (x - mean)).Sum();
        var variance = sumSqrtDiff / (values.Length - 1);

        return (int)variance;
    }

    public async Task InitializeAsync()
    {
        // Clear any previous events from prior tests
        RebalancerObserver.Clear();

        await GrainFactory
            .GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.Zero);
    }

    public Task DisposeAsync()
    {
        RebalancerObserver.Dispose();
        return Task.CompletedTask;
    }
}

public interface IRebalancingTestGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class RebalancingTestGrain : Grain, IRebalancingTestGrain
{
    public Task Ping() => Task.CompletedTask;
}