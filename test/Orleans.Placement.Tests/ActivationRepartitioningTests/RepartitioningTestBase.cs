using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

public abstract class RepartitioningTestBase<TFixture> : IAsyncLifetime where TFixture : BaseTestClusterFixture, new()
{
    private readonly TFixture _fixture;

    internal IInternalGrainFactory GrainFactory => _fixture.HostedCluster.InternalGrainFactory!;
    internal IActivationRepartitionerSystemTarget Silo1Repartitioner { get; }
    internal IActivationRepartitionerSystemTarget Silo2Repartitioner { get; }
    protected SiloAddress Silo1 { get; }
    protected SiloAddress Silo2 { get; }

    public RepartitioningTestBase(TFixture fixture)
    {
        _fixture = fixture;

        var silos = _fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s).ToArray();
        Silo1 = silos[0];
        Silo2 = silos[1];

        Silo1Repartitioner = IActivationRepartitionerSystemTarget.GetReference(GrainFactory, Silo1);
        Silo2Repartitioner = IActivationRepartitionerSystemTarget.GetReference(GrainFactory, Silo2);
    }

    public virtual async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await GrainFactory.GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.FromSeconds(0))
            .WaitAsync(cancellationToken);
        await ResetCounters(cancellationToken);
        await AdjustActivationCountOffsets(cancellationToken);
    }

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask ResetCounters(CancellationToken cancellationToken)
    {
        await Silo1Repartitioner.ResetCounters(cancellationToken);
        await Silo2Repartitioner.ResetCounters(cancellationToken);
    }

    private protected async ValueTask TriggerExchangeRequestAfterFlushingBuffers(
        IActivationRepartitionerSystemTarget repartitioner,
        CancellationToken cancellationToken)
    {
        await Silo1Repartitioner.FlushBuffers(cancellationToken);
        await Silo2Repartitioner.FlushBuffers(cancellationToken);
        await repartitioner.TriggerExchangeRequest(cancellationToken);
    }

    private protected static Task WaitForConditionAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        Func<string> timeoutMessage,
        CancellationToken cancellationToken)
        => TestingUtils.WaitUntilAsync(
            async (lastTry, _) =>
            {
                var succeeded = await predicate();
                if (lastTry)
                {
                    Assert.True(succeeded, timeoutMessage());
                }

                return succeeded;
            },
            timeout,
            TimeSpan.FromMilliseconds(10),
            cancellationToken);

    public async Task AdjustActivationCountOffsets(CancellationToken cancellationToken)
    {
        // Account for imbalances in the initial activation counts.
        Dictionary<SiloAddress, int> counts = [];
        int max = 0;
        foreach (var silo in (IEnumerable<SiloHandle>)_fixture.HostedCluster.Silos)
        {
            var sysTarget = GrainFactory.GetSystemTarget<IActivationRepartitionerSystemTarget>(Constants.ActivationRepartitionerType, silo.SiloAddress);
            var count = counts[silo.SiloAddress] = await sysTarget.GetActivationCount(cancellationToken);
            max = Math.Max(max, count);
        }

        foreach (var silo in (IEnumerable<SiloHandle>)_fixture.HostedCluster.Silos)
        {
            var sysTarget = GrainFactory.GetSystemTarget<IActivationRepartitionerSystemTarget>(Constants.ActivationRepartitionerType, silo.SiloAddress);
            var myCount = counts[silo.SiloAddress];
            await sysTarget.SetActivationCountOffset(max - myCount, cancellationToken);
        }
    }

}
