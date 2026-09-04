using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Reminders.TestKit.Tests;

/// <summary>
/// Verifies that the idealized oracle can be installed into every silo of an <see cref="InProcessTestCluster"/>
/// and that it exposes exactly what the reminder service persisted.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders"), TestCategory("ReminderTestKit")]
public sealed class ReminderTestKitClusterIntegrationTests
{
    [Fact]
    public async Task ReminderTestKit_ClusterUsesOneOracleInstanceInEverySilo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(2);
        var oracle = builder.UseIdealizedReminderTable();
        var cluster = builder.Build();

        try
        {
            await cluster.DeployAsync(cancellationToken);

            Assert.Equal(2, cluster.Silos.Count);
            foreach (var silo in cluster.Silos)
            {
                Assert.Same(oracle, silo.ServiceProvider.GetRequiredService<IReminderTable>());
                Assert.Same(oracle, silo.ServiceProvider.GetRequiredService<IdealizedReminderTable>());
            }
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_ClusterRegistrationAndUnregistrationAreVisibleInTheOracle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable();
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, cancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var period = TimeSpan.FromMinutes(5);

            var registeredName = await grain.RegisterReminderAsync("cluster-reminder", period, period).WaitAsync(cancellationToken);

            var persisted = Assert.Single(oracle.Snapshot());
            Assert.Equal("cluster-reminder", registeredName);
            Assert.Equal("cluster-reminder", persisted.ReminderName);
            Assert.Equal(grainId, persisted.GrainId);
            Assert.Equal(period, persisted.Period);
            Assert.Equal(1, persisted.Version);
            Assert.False(string.IsNullOrEmpty(persisted.ETag));
            Assert.Null(persisted.PreviousETag);
            Assert.Equal(["cluster-reminder"], await grain.GetReminderNamesAsync().WaitAsync(cancellationToken));

            var unregistered = await grain.UnregisterReminderAsync("cluster-reminder").WaitAsync(cancellationToken);

            Assert.True(unregistered);
            Assert.Empty(oracle.Snapshot());
            Assert.Empty(await grain.GetReminderNamesAsync().WaitAsync(cancellationToken));
            Assert.Contains(
                oracle.Operations,
                operation => operation.Kind == ReminderTableOperationKind.RemoveRow
                    && operation.GrainId == grainId
                    && operation.ReminderName == "cluster-reminder"
                    && operation.Succeeded);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_ClusterReminderUpdateRotatesTheOracleETagWithoutDuplicatingTheRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable();
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, cancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());

            await grain.RegisterReminderAsync("updated-reminder", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)).WaitAsync(cancellationToken);
            var initial = Assert.Single(oracle.Snapshot());

            await grain.RegisterReminderAsync("updated-reminder", TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(9)).WaitAsync(cancellationToken);
            var updated = Assert.Single(oracle.Snapshot());

            Assert.Equal(TimeSpan.FromMinutes(5), initial.Period);
            Assert.Equal(TimeSpan.FromMinutes(9), updated.Period);
            Assert.Equal(initial.ETag, updated.PreviousETag);
            Assert.NotEqual(initial.ETag, updated.ETag);
            Assert.Equal(2, updated.Version);
            Assert.Equal(initial.GrainId, updated.GrainId);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_StorageOutageFailsRegistrationAndRecoveryAllowsRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable();
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, cancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());

            oracle.SetAvailable(false);
            var failure = await Assert.ThrowsAsync<ReminderTableUnavailableException>(
                () => grain.RegisterReminderAsync("outage-reminder", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)).WaitAsync(cancellationToken));

            Assert.Contains("simulated storage outage", failure.Message, StringComparison.Ordinal);
            Assert.Empty(oracle.Snapshot());
            Assert.Contains(
                oracle.Operations,
                operation => operation.Kind == ReminderTableOperationKind.UpsertRow
                    && operation.ReminderName == "outage-reminder"
                    && !operation.Succeeded
                    && operation.Failure == nameof(ReminderTableUnavailableException));

            oracle.SetAvailable(true);
            var registered = await grain.RegisterReminderAsync("outage-reminder", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)).WaitAsync(cancellationToken);

            Assert.Equal("outage-reminder", registered);
            var record = Assert.Single(oracle.Snapshot());
            Assert.Equal("outage-reminder", record.ReminderName);
            Assert.Equal(TimeSpan.FromMinutes(5), record.Period);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_FakeClockFiresAtExactDueTimeWithoutSleeping()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable();
        using var clock = ReminderTestClock.Attach(
            builder,
            minimumReminderPeriod: TimeSpan.FromSeconds(1),
            refreshReminderListPeriod: TimeSpan.FromSeconds(1));
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, testCancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));

            await grain.RegisterReminderAsync("exact-due", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)).WaitAsync(cancellation.Token);
            await observer.WaitForLocalReminderScheduleAsync(grainId, "exact-due", cancellation.Token);

            var tick = observer.WaitForReminderTickAsync(grainId, cancellation.Token, "exact-due");
            await clock.AdvanceAsync(TimeSpan.FromSeconds(4), cancellation.Token);
            Assert.False(tick.IsCompleted);

            await clock.AdvanceAsync(TimeSpan.FromSeconds(1), cancellation.Token);
            var completed = await tick;

            Assert.Equal(grainId, completed.GrainId);
            Assert.Equal("exact-due", completed.ReminderName);
            Assert.Equal(1, observer.GetTickCount(grainId, "exact-due"));
            Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(oracle.Snapshot()).Period);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_DueTimeBeyondTimerLimitLoadsAndFiresOnPersistedSchedule()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var loadingWindow = TimeSpan.FromSeconds(5);
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable(
            configureReminderOptions: options => options.ReminderLoadingWindow = loadingWindow);
        using var clock = ReminderTestClock.Attach(
            builder,
            minimumReminderPeriod: TimeSpan.FromSeconds(1),
            refreshReminderListPeriod: TimeSpan.FromSeconds(1));
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, testCancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var timerLimit = TimeSpan.FromMilliseconds(0xfffffffe) + TimeSpan.FromMilliseconds(1);
            var dueTime = timerLimit + TimeSpan.FromDays(1);
            var firstTickTime = clock.UtcNow.UtcDateTime + dueTime;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));

            await grain.RegisterReminderAsync("long-due", dueTime, dueTime).WaitAsync(cancellation.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, "long-due"));
            var activated = observer.WaitForActiveReminderCountAsync(grainId, 1, cancellation.Token, "long-due");
            await clock.AdvanceAsync(dueTime - loadingWindow + clock.RefreshReminderListPeriod, cancellation.Token);
            await activated;
            await observer.WaitForLocalReminderScheduleAsync(grainId, "long-due", cancellation.Token);

            var tick = observer.WaitForReminderTickAsync(grainId, cancellation.Token, "long-due");
            var remaining = firstTickTime - clock.UtcNow.UtcDateTime;
            Assert.True(remaining > TimeSpan.Zero);
            await clock.AdvanceAsync(remaining, cancellation.Token);
            var completed = await tick;

            Assert.Equal(firstTickTime, completed.Status.FirstTickTime);
            Assert.Equal(firstTickTime, completed.Status.CurrentTickTime);
            Assert.Equal(1, observer.GetTickCount(grainId, "long-due"));
            Assert.Equal(dueTime, Assert.Single(oracle.Snapshot()).Period);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_StorageRecoveryAtExactDueTimeDeliversDueOccurrence()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var loadingWindow = TimeSpan.FromSeconds(10);
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable(
            configureReminderOptions: options => options.ReminderLoadingWindow = loadingWindow);
        using var clock = ReminderTestClock.Attach(
            builder,
            minimumReminderPeriod: TimeSpan.FromSeconds(1),
            refreshReminderListPeriod: TimeSpan.FromSeconds(1));
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, testCancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = loadingWindow + TimeSpan.FromMinutes(1);
            var period = TimeSpan.FromSeconds(30);
            var firstTickTime = clock.UtcNow.UtcDateTime + dueTime;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));

            await grain.RegisterReminderAsync("exact-due-recovery", dueTime, period).WaitAsync(cancellation.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, "exact-due-recovery"));

            await using var blockedRead = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            await AdvanceUntilAsync(clock, blockedRead.WaitUntilBlockedAsync(cancellation.Token), cancellation.Token);
            var remaining = firstTickTime - clock.UtcNow.UtcDateTime;
            Assert.True(remaining > TimeSpan.Zero);
            await clock.AdvanceAsync(remaining, cancellation.Token);

            var activated = observer.WaitForActiveReminderCountAsync(grainId, 1, cancellation.Token, "exact-due-recovery");
            blockedRead.Release();
            await activated;
            await observer.WaitForLocalReminderScheduleAsync(grainId, "exact-due-recovery", cancellation.Token);
            Assert.Equal(["exact-due-recovery"], await grain.GetReminderNamesAsync().WaitAsync(cancellation.Token));

            var tick = observer.WaitForReminderTickAsync(grainId, cancellation.Token, "exact-due-recovery");
            await clock.AdvanceAsync(clock.RefreshReminderListPeriod, cancellation.Token);
            var completed = await tick;

            Assert.Equal(firstTickTime, completed.Status.FirstTickTime);
            Assert.Equal(firstTickTime + clock.RefreshReminderListPeriod, completed.Status.CurrentTickTime);
            Assert.Equal(1, observer.GetTickCount(grainId, "exact-due-recovery"));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_StaleRefreshCannotRestoreUnregisteredReminder()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        var oracle = builder.UseIdealizedReminderTable(
            configureReminderOptions: options => options.ReminderLoadingWindow = TimeSpan.FromHours(2));
        using var clock = ReminderTestClock.Attach(
            builder,
            minimumReminderPeriod: TimeSpan.FromSeconds(1),
            refreshReminderListPeriod: TimeSpan.FromSeconds(1));
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStartupReminderTopologyAsync(cluster, observer, testCancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));

            var activated = observer.WaitForActiveReminderCountAsync(grainId, 1, cancellation.Token, "stale-unregister");
            await grain.RegisterReminderAsync("stale-unregister", TimeSpan.FromHours(1), TimeSpan.FromHours(2)).WaitAsync(cancellation.Token);
            await activated;
            await observer.WaitForLocalReminderScheduleAsync(grainId, "stale-unregister", cancellation.Token);

            await using var staleRead = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            await using var followingRead = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            using (oracle.FreezeReads())
            {
                await AdvanceUntilAsync(clock, staleRead.WaitUntilBlockedAsync(cancellation.Token), cancellation.Token);

                var quiescence = observer.WaitForReminderQuiescenceAsync(grainId, "stale-unregister", cancellation.Token);
                Assert.True(await grain.UnregisterReminderAsync("stale-unregister").WaitAsync(cancellation.Token));
                await quiescence;
                staleRead.Release();

                await AdvanceUntilAsync(clock, followingRead.WaitUntilBlockedAsync(cancellation.Token), cancellation.Token);

                Assert.Equal(0, observer.GetActiveReminderCount(grainId, "stale-unregister"));
                Assert.Equal(0, observer.GetTickCount(grainId, "stale-unregister"));
                Assert.Empty(oracle.Snapshot());
            }

            followingRead.Release();
            Assert.Empty(await grain.GetReminderNamesAsync().WaitAsync(cancellation.Token));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task ReminderTestKit_TwoSilosMaintainOneOwnerAndOneDelivery()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
            siloBuilder.Configure<ConsistentRingOptions>(options => options.UseVirtualBucketsConsistentRing = false));
        var oracle = builder.UseIdealizedReminderTable();
        using var clock = ReminderTestClock.Attach(
            builder,
            minimumReminderPeriod: TimeSpan.FromSeconds(1),
            refreshReminderListPeriod: TimeSpan.FromSeconds(1));
        var cluster = builder.Build();
        using var observer = ReminderDiagnosticObserver.Create(cluster);

        try
        {
            await DeployAndWaitForStableTwoSiloReminderTopologyAsync(cluster, observer, testCancellationToken);
            var grain = cluster.Client.GetGrain<IReminderTestKitGrain>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = TimeSpan.FromMinutes(10);
            var firstTickTime = clock.UtcNow.UtcDateTime + dueTime;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));

            await using var firstRefreshA = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            await using var firstRefreshB = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            await using var followingRefreshA = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            await using var followingRefreshB = oracle.BlockNext(ReminderTableOperationKind.ReadRange);
            var firstRefreshABlocked = firstRefreshA.WaitUntilBlockedAsync(cancellation.Token);
            var firstRefreshBBlocked = firstRefreshB.WaitUntilBlockedAsync(cancellation.Token);
            var followingRefreshABlocked = followingRefreshA.WaitUntilBlockedAsync(cancellation.Token);
            var followingRefreshBBlocked = followingRefreshB.WaitUntilBlockedAsync(cancellation.Token);
            var phase = "registration";
            try
            {
                var oneOwner = observer.WaitForActiveReminderCountAsync(grainId, 1, cancellation.Token, "single-owner");
                await grain.RegisterReminderAsync("single-owner", dueTime, TimeSpan.FromMinutes(2)).WaitAsync(cancellation.Token);

                phase = "first refresh wave";
                await AdvanceUntilAsync(
                    clock,
                    Task.WhenAll(firstRefreshABlocked, firstRefreshBBlocked),
                    cancellation.Token);
                firstRefreshA.Release();
                firstRefreshB.Release();

                phase = "following refresh wave";
                await AdvanceUntilAsync(
                    clock,
                    Task.WhenAll(followingRefreshABlocked, followingRefreshBBlocked),
                    cancellation.Token);

                phase = "single owner reconciliation";
                await oneOwner;
                await observer.WaitForLocalReminderScheduleAsync(grainId, "single-owner", cancellation.Token);

                Assert.Single(observer.GetActiveReminderSilos(grainId, "single-owner"));
                var tick = observer.WaitForReminderTickAsync(grainId, cancellation.Token, "single-owner");
                var remaining = firstTickTime - clock.UtcNow.UtcDateTime;
                Assert.True(remaining > TimeSpan.Zero);
                phase = "exact due delivery";
                await clock.AdvanceAsync(remaining, cancellation.Token);
                var completed = await tick;
                await observer.WaitForLocalReminderScheduleAsync(grainId, "single-owner", cancellation.Token);

                Assert.Equal(firstTickTime, completed.Status.CurrentTickTime);
                Assert.Equal(1, observer.GetActiveReminderCount(grainId, "single-owner"));
                Assert.Equal(1, observer.GetTickCount(grainId, "single-owner"));
                Assert.Single(oracle.Snapshot());
            }
            catch (OperationCanceledException exception) when (
                cancellation.IsCancellationRequested
                && !testCancellationToken.IsCancellationRequested)
            {
                var owners = observer.GetActiveReminderSilos(grainId, "single-owner");
                var operations = oracle.Operations.TakeLast(8);
                throw new TimeoutException(
                    $"Two-silo reminder scenario timed out during '{phase}'. "
                    + $"blockedReads=[firstA={firstRefreshABlocked.IsCompleted}, firstB={firstRefreshBBlocked.IsCompleted}, "
                    + $"followingA={followingRefreshABlocked.IsCompleted}, followingB={followingRefreshBBlocked.IsCompleted}], "
                    + $"owners=[{string.Join(", ", owners.Select(silo => silo.ToString()))}], "
                    + $"operations=[{string.Join("; ", operations)}].",
                    exception);
            }
            finally
            {
                firstRefreshA.Release();
                firstRefreshB.Release();
                followingRefreshA.Release();
                followingRefreshB.Release();
            }
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    private static async Task DisposeClusterAsync(InProcessTestCluster cluster)
    {
        try
        {
            using var stopCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await cluster.StopAllSilosAsync(stopCancellation.Token);
        }
        finally
        {
            using var disposeCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await cluster.DisposeAsync().AsTask().WaitAsync(disposeCancellation.Token);
        }
    }

    private static async Task DeployAndWaitForStartupReminderTopologyAsync(
        InProcessTestCluster cluster,
        ReminderDiagnosticObserver observer,
        CancellationToken cancellationToken)
    {
        await cluster.DeployAsync(cancellationToken);
        using var topologyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        topologyCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
                cluster,
                observer,
                cluster.Silos,
                topologyCancellation.Token);
        }
        catch (OperationCanceledException exception) when (
            topologyCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(exception.Message, exception);
        }
    }

    private static async Task DeployAndWaitForStableTwoSiloReminderTopologyAsync(
        InProcessTestCluster cluster,
        ReminderDiagnosticObserver observer,
        CancellationToken cancellationToken)
    {
        await cluster.DeployAsync(cancellationToken);
        var initialSilo = Assert.Single(cluster.Silos);
        using var topologyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        topologyCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
                cluster,
                observer,
                [initialSilo],
                topologyCancellation.Token);
            var joinedSilo = Assert.Single(await cluster.StartSilosAsync(1, topologyCancellation.Token));
            await ReminderTopologyStabilizer.WaitForStableTopologyAsync(
                cluster,
                observer,
                [initialSilo, joinedSilo],
                topologyCancellation.Token);
        }
        catch (OperationCanceledException exception) when (
            topologyCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(exception.Message, exception);
        }
    }

    private static async Task AdvanceUntilAsync(
        ReminderTestClock clock,
        Task condition,
        CancellationToken cancellationToken)
    {
        while (!condition.IsCompleted)
        {
            await clock.AdvanceAsync(clock.RefreshReminderListPeriod, cancellationToken);
            await Task.WhenAny(condition, Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken));
        }

        await condition;
    }
}
