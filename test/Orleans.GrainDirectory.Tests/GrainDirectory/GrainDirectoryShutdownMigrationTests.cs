#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryShutdownMigrationTests
{
    [Fact]
    public async Task PreferLocalGrain_MigratesWhenHostingSiloShutsDown()
    {
        var builder = new InProcessTestClusterBuilder(2);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton<MembershipTableManager>();
            siloBuilder.Services.AddSingleton<IMembershipManager, DelayedMembershipManager>();
#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync();
        await cluster.WaitForLivenessToStabilizeAsync();
        await WaitForDirectoryMembershipAsync(cluster);

        var survivingSilo = cluster.Silos[0];
        var shuttingDownSilo = cluster.Silos[1];
        var immediateGrain = cluster.Client.GetGrain<IShutdownMigrationGrain>(Guid.NewGuid());
        var postHandoffGrain = cluster.Client.GetGrain<IShutdownMigrationGrain>(Guid.NewGuid());

        RequestContext.Set(IPlacementDirector.PlacementHintKey, shuttingDownSilo.SiloAddress);
        try
        {
            await immediateGrain.SetState(42, waitForDirectoryHandoff: false);
            await postHandoffGrain.SetState(43, waitForDirectoryHandoff: true);
            Assert.Equal(shuttingDownSilo.SiloAddress, await immediateGrain.GetLocation());
            Assert.Equal(shuttingDownSilo.SiloAddress, await postHandoffGrain.GetLocation());
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        ShutdownMigrationTestCoordinator.Reset();
        var stopTask = cluster.StopSiloAsync(shuttingDownSilo);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await WaitForDirectoryMembershipAsync(
                survivingSilo.ServiceProvider.GetRequiredService<DirectoryMembershipService>(),
                [survivingSilo.SiloAddress],
                timeout.Token);
        }
        finally
        {
            ShutdownMigrationTestCoordinator.CompleteDirectoryHandoff();
            await stopTask;
        }

        await cluster.WaitForLivenessToStabilizeAsync();

        await AssertMigrated(immediateGrain, 42);
        await AssertMigrated(postHandoffGrain, 43);

        async Task AssertMigrated(IShutdownMigrationGrain grain, int expectedState)
        {
            Assert.Equal(survivingSilo.SiloAddress, await grain.GetLocation());
            Assert.Equal(expectedState, await grain.GetState());
            Assert.Equal(DeactivationReasonCode.ShuttingDown, await grain.GetPreviousDeactivationReason());
            Assert.Equal(SiloStatus.ShuttingDown, await grain.GetPreviousSiloStatus());
        }
    }

    private static async Task WaitForDirectoryMembershipAsync(InProcessTestCluster cluster)
    {
        var expectedMembers = cluster.Silos.Select(static silo => silo.SiloAddress).ToHashSet();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(cluster.Silos.Select(
            silo => WaitForDirectoryMembershipAsync(
                silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>(),
                expectedMembers,
                timeout.Token)));
    }

    private static async Task WaitForDirectoryMembershipAsync(
        DirectoryMembershipService membershipService,
        HashSet<SiloAddress> expectedMembers,
        CancellationToken cancellationToken)
    {
        if (expectedMembers.SetEquals(membershipService.CurrentView.Members))
        {
            return;
        }

        try
        {
            await foreach (var view in membershipService.ViewUpdates.WithCancellation(cancellationToken))
            {
                if (expectedMembers.SetEquals(view.Members))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the distributed grain directory membership to stabilize.", exception);
        }

        throw new TimeoutException("Timed out waiting for the distributed grain directory membership to stabilize.");
    }
}

public interface IShutdownMigrationGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();

    ValueTask<int> GetState();

    ValueTask<DeactivationReasonCode> GetPreviousDeactivationReason();

    ValueTask<SiloStatus> GetPreviousSiloStatus();

    ValueTask SetState(int value, bool waitForDirectoryHandoff);
}

[PreferLocalPlacement]
public sealed class ShutdownMigrationGrain : Grain, IShutdownMigrationGrain, IGrainMigrationParticipant
{
    private readonly ISiloStatusOracle _siloStatusOracle;
    private int _state;
    private DeactivationReasonCode _previousDeactivationReason;
    private SiloStatus _previousSiloStatus;
    private bool _waitForDirectoryHandoff;

    public ShutdownMigrationGrain(ISiloStatusOracle siloStatusOracle)
    {
        _siloStatusOracle = siloStatusOracle;
    }

    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);

    public ValueTask<int> GetState() => new(_state);

    public ValueTask<DeactivationReasonCode> GetPreviousDeactivationReason() => new(_previousDeactivationReason);

    public ValueTask<SiloStatus> GetPreviousSiloStatus() => new(_previousSiloStatus);

    public ValueTask SetState(int value, bool waitForDirectoryHandoff)
    {
        _state = value;
        _waitForDirectoryHandoff = waitForDirectoryHandoff;
        return default;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (reason.ReasonCode is DeactivationReasonCode.ShuttingDown)
        {
            _previousDeactivationReason = reason.ReasonCode;
            _previousSiloStatus = _siloStatusOracle.CurrentStatus;
            if (_waitForDirectoryHandoff)
            {
                await ShutdownMigrationTestCoordinator.WaitForDirectoryHandoff(cancellationToken);
            }

            this.MigrateOnIdle();
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public void OnDehydrate(IDehydrationContext migrationContext)
    {
        migrationContext.TryAddValue("state", _state);
        migrationContext.TryAddValue("deactivation-reason", _previousDeactivationReason);
        migrationContext.TryAddValue("silo-status", _previousSiloStatus);
    }

    public void OnRehydrate(IRehydrationContext migrationContext)
    {
        migrationContext.TryGetValue("state", out _state);
        migrationContext.TryGetValue("deactivation-reason", out _previousDeactivationReason);
        migrationContext.TryGetValue("silo-status", out _previousSiloStatus);
    }
}

internal static class ShutdownMigrationTestCoordinator
{
    private static TaskCompletionSource<bool> _directoryHandoff = CreateCompletedSource();

    public static void Reset() =>
        Interlocked.Exchange(ref _directoryHandoff, new(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void CompleteDirectoryHandoff() =>
        Volatile.Read(ref _directoryHandoff).TrySetResult(true);

    public static Task WaitForDirectoryHandoff(CancellationToken cancellationToken) =>
        Volatile.Read(ref _directoryHandoff).Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(true);
        return result;
    }
}

internal sealed class DelayedMembershipManager(MembershipTableManager inner) : IMembershipManager
{
    public MembershipTableSnapshot CurrentSnapshot => ((IMembershipManager)inner).CurrentSnapshot;

    public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => ((IMembershipManager)inner).MembershipUpdates;

    public SiloStatus LocalSiloStatus => ((IMembershipManager)inner).LocalSiloStatus;

    public bool CheckHealth(DateTime lastCheckTime, out string reason) =>
        ((IHealthCheckable)inner).CheckHealth(lastCheckTime, out reason);

    public void Participate(ISiloLifecycle lifecycle) => ((ILifecycleParticipant<ISiloLifecycle>)inner).Participate(lifecycle);

    public Task ProcessGossipSnapshot(MembershipTableSnapshot snapshot, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).ProcessGossipSnapshot(snapshot, cancellationToken);

    public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).Refresh(targetVersion, cancellationToken);

    public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).TryKillSilo(silo, cancellationToken);

    public Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress? indirectProbingSilo, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).TrySuspectSilo(silo, indirectProbingSilo, cancellationToken);

    public Task UpdateIAmAlive(CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).UpdateIAmAlive(cancellationToken);

    public async Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken)
    {
        if (status is SiloStatus.ShuttingDown)
        {
            // Model a membership provider which takes long enough to expose concurrent shutdown callbacks.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // End the artificial delay promptly, but let the real manager publish the requested status.
            }
        }

        await ((IMembershipManager)inner).UpdateLocalStatus(status, cancellationToken);
    }
}
