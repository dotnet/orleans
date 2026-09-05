using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

public class LocalReminderServiceTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CalculateFollowingTickTime_ReturnsNextScheduledOccurrence()
    {
        var period = TimeSpan.FromDays(60);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateFollowingTickTime(entry, startAt, startAt);

        Assert.Equal(startAt + period, nextTick);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CalculateFollowingTickTime_SkipsMissedOccurrencesWithoutDrifting()
    {
        var period = TimeSpan.FromMinutes(10);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateFollowingTickTime(
            entry,
            startAt,
            startAt + TimeSpan.FromMinutes(25));

        Assert.Equal(startAt + TimeSpan.FromMinutes(30), nextTick);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void CalculateNextTickTime_PreservesCadenceAtOccurrenceBoundary(int offsetTicks, int expectedAdditionalPeriods)
    {
        var period = TimeSpan.FromMinutes(10);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boundary = startAt + (2 * period);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateNextTickTime(entry, boundary.AddTicks(offsetTicks));

        Assert.Equal(boundary + (expectedAdditionalPeriods * period), nextTick);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CalculateNextTickTime_TreatsPersistedUnspecifiedTimestampAsUtc()
    {
        var persistedStartAt = new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Unspecified);
        var entry = CreateReminderEntry(persistedStartAt, TimeSpan.FromMinutes(10));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var nextTick = LocalReminderService.CalculateNextTickTime(entry, now);

        Assert.Equal(DateTimeKind.Utc, nextTick.Kind);
        Assert.Equal(persistedStartAt.Ticks, nextTick.Ticks);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IsReminderWithinLoadingWindow_UsesInclusiveBoundary(int millisecondsInsideWindow, bool expected)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var loadingWindow = TimeSpan.FromMinutes(10);
        var entry = CreateReminderEntry(now + loadingWindow + TimeSpan.FromMilliseconds(-millisecondsInsideWindow), TimeSpan.FromHours(1));

        var result = LocalReminderService.IsReminderWithinLoadingWindow(entry, now, loadingWindow);

        Assert.Equal(expected, result);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(true, 0, true, true)]
    [InlineData(false, 0, true, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 1, true, false)]
    public void ShouldRetainFiredOccurrenceTombstone_RequiresMatchingFiredTickAndSchedule(
        bool hasFiredTick,
        int nextTickOffsetTicks,
        bool sameETag,
        bool expected)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var localEntry = CreateReminderEntry(now, TimeSpan.FromMinutes(10));
        var tableEntry = CreateReminderEntry(now, TimeSpan.FromMinutes(10));
        if (!sameETag)
        {
            tableEntry.ETag = "updated-etag";
        }

        var result = LocalReminderService.ShouldRetainFiredOccurrenceTombstone(
            localEntry,
            tableEntry,
            hasFiredTick ? now : null,
            now.AddTicks(nextTickOffsetTicks));

        Assert.Equal(expected, result);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ReminderOptions_DefaultLoadingWindowIsTwiceDefaultRefreshPeriod()
    {
        var options = new ReminderOptions();

        Assert.Equal(2 * options.RefreshReminderListPeriod, options.ReminderLoadingWindow);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ReminderOptionsValidator_RejectsNonPositiveLoadingWindow()
    {
        var options = new ReminderOptions { ReminderLoadingWindow = TimeSpan.Zero };

        Assert.Throws<OrleansConfigurationException>(() => Validate(options));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ReminderOptionsValidator_RejectsLoadingWindowShorterThanRefreshPeriod(int differenceInSeconds, bool throws)
    {
        var refreshPeriod = TimeSpan.FromMinutes(5);
        var options = new ReminderOptions
        {
            RefreshReminderListPeriod = refreshPeriod,
            ReminderLoadingWindow = refreshPeriod + TimeSpan.FromSeconds(differenceInSeconds),
        };

        var exception = Record.Exception(() => Validate(options));

        Assert.Equal(throws, exception is OrleansConfigurationException);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AddReminders_RegistersReminderOptionsValidatorOnce_AndValidatesInvalidConfigurationThroughDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<ReminderOptions>(options =>
        {
            options.RefreshReminderListPeriod = TimeSpan.FromMinutes(5);
            options.ReminderLoadingWindow = TimeSpan.FromMinutes(1);
        });

        // AddReminders must be idempotent: calling it twice must not register the validator twice.
        services.AddReminders();
        services.AddReminders();

        var validatorRegistrations = services.Where(descriptor => descriptor.ServiceType == typeof(IConfigurationValidator)).ToList();
        Assert.Single(validatorRegistrations);

        using var serviceProvider = services.BuildServiceProvider();
        var validators = serviceProvider.GetServices<IConfigurationValidator>().ToList();
        Assert.Single(validators);
        Assert.IsType<ReminderOptionsValidator>(validators[0]);

        // The invalid configuration must be rejected via the resolved IConfigurationValidator, not by
        // constructing ReminderOptionsValidator directly.
        var exception = Assert.Throws<OrleansConfigurationException>(() =>
        {
            foreach (IConfigurationValidator resolvedValidator in validators)
            {
                resolvedValidator.ValidateConfiguration();
            }
        });

        Assert.Contains(nameof(ReminderOptions.ReminderLoadingWindow), exception.Message);
        Assert.Contains(nameof(ReminderOptions.RefreshReminderListPeriod), exception.Message);
    }

    private static void Validate(ReminderOptions options)
    {
        var validator = new ReminderOptionsValidator(
            NullLogger<ReminderOptionsValidator>.Instance,
            Options.Create(options));
        validator.ValidateConfiguration();
    }

    private static ReminderEntry CreateReminderEntry(DateTime startAt, TimeSpan period)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "reminder",
            StartAt = startAt,
            Period = period,
            ETag = "etag",
        };
    }
}

public class LocalReminderServiceCompatibilityTests : IClassFixture<LocalReminderServiceCompatibilityTests.Fixture>
{
    private readonly Fixture fixture;

    public LocalReminderServiceCompatibilityTests(Fixture fixture)
    {
        this.fixture = fixture;
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task InitialRead_TreatsNullTableResultAsNoWork()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);

        var started = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        Assert.Equal([silo.SiloAddress], started);
        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        Assert.True(reminderTable.RangeReadCount > 0);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task InitialRead_RetriesWhenRangeChangeIsQueuedBehindReadCompletion()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        var firstRead = new RangeReadGate(cancellation.Token);
        NullReturningReminderTable? initialTable = null;
        var builder = new InProcessTestClusterBuilder(1);
        var clock = builder.AddReminderTestClock();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            var table = new NullReturningReminderTable(initialTable is null ? firstRead : null);
            initialTable ??= table;

            siloBuilder.AddReminders();
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton(table);
                services.AddSingleton<IReminderTable>(
                    static provider => provider.GetRequiredService<NullReturningReminderTable>());
            });
        });

        var cluster = builder.Build();
        InProcessSiloHandle? joinedSilo = null;
        try
        {
            await cluster.DeployAsync(cancellation.Token);
            await firstRead.WaitUntilBlockedAsync(cancellation.Token);
            var initialSilo = Assert.Single(cluster.Silos);
            var reminderService = initialSilo.ServiceProvider.GetRequiredService<LocalReminderService>();
            var schedulerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseScheduler = new ManualResetEventSlim();
            var blockingTask = new Task(() =>
            {
                schedulerBlocked.TrySetResult();
                releaseScheduler.Wait(cancellation.Token);
            });
            reminderService.Scheduler.QueueTask(blockingTask);
            await schedulerBlocked.Task.WaitAsync(cancellation.Token);

            var secondRead = Assert.IsType<NullReturningReminderTable>(initialTable)
                .BlockNextRangeRead(cancellation.Token);
            try
            {
                firstRead.Release();
                joinedSilo = Assert.Single(await cluster.StartSilosAsync(1, cancellation.Token));
                await reminderService.TestOnlyWaitForSiloStatusListeners(cancellation.Token);
                var started = clock.DiagnosticObserver.WaitForReminderServiceStartedAsync(
                    cancellation.Token,
                    initialSilo.SiloAddress);

                releaseScheduler.Set();
                await blockingTask.WaitAsync(cancellation.Token);
                await secondRead.WaitUntilBlockedAsync(cancellation.Token);
                Assert.False(started.IsCompleted);

                secondRead.Release();
                await started;
            }
            finally
            {
                releaseScheduler.Set();
                firstRead.Release();
                secondRead.Release();
                await blockingTask.WaitAsync(cancellation.Token);
            }
        }
        finally
        {
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                if (joinedSilo is not null)
                {
                    await cluster.StopSiloAsync(joinedSilo, cleanupCancellation.Token);
                }

                await cluster.StopAllSilosAsync(cleanupCancellation.Token);
            }
            finally
            {
                try
                {
                    using var disposeCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                    await cluster.DisposeAsync().AsTask().WaitAsync(disposeCancellation.Token);
                }
                finally
                {
                    clock.Dispose();
                }
            }
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task SiloStatusListenerBarrier_WaitsForCurrentMembershipVersion()
    {
        var initialSilo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        using var listener = new BlockingSiloStatusListener(initialSilo.SiloAddress);
        var statusOracle = initialSilo.ServiceProvider.GetRequiredService<ISiloStatusOracle>();
        Assert.True(statusOracle.SubscribeToSiloStatusEvents(listener));
        Task<List<InProcessSiloHandle>>? startTask = null;
        InProcessSiloHandle? joinedSilo = null;
        try
        {
            startTask = fixture.HostedCluster.StartSilosAsync(1, cancellation.Token);
            await listener.WaitUntilBlockedAsync(cancellation.Token);

            var reminderService = initialSilo.ServiceProvider.GetRequiredService<LocalReminderService>();
            var barrier = reminderService.TestOnlyWaitForSiloStatusListeners(cancellation.Token);
            Assert.False(barrier.IsCompleted);

            listener.Release();
            joinedSilo = Assert.Single(await startTask.WaitAsync(cancellation.Token));
            await barrier;
        }
        finally
        {
            listener.Release();
            statusOracle.UnSubscribeFromSiloStatusEvents(listener);
            if (joinedSilo is null && startTask is not null)
            {
                using var startupCleanup = new CancellationTokenSource(TestConstants.InitTimeout);
                joinedSilo = Assert.Single(await startTask.WaitAsync(startupCleanup.Token));
            }

            if (joinedSilo is not null)
            {
                using var siloCleanup = new CancellationTokenSource(TestConstants.InitTimeout);
                await fixture.HostedCluster.StopSiloAsync(joinedSilo, siloCleanup.Token);
                await fixture.HostedCluster.WaitForLivenessToStabilizeAsync();
            }
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_FollowsLatestReconciliation()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var intermediateRange = RangeFactory.CreateRange(0, uint.MaxValue / 3);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var firstReadGate = reminderTable.BlockNextRangeRead(cancellation.Token);
        RangeReadGate? secondReadGate = null;
        try
        {
            var firstRangeChangeTask = reminderService.TestOnlyChangeRange(oldRange, intermediateRange, increased: false);
            await firstReadGate.WaitUntilBlockedAsync(cancellation.Token);

            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            secondReadGate = reminderTable.BlockNextRangeRead(cancellation.Token);
            var secondRangeChangeTask = reminderService.TestOnlyChangeRange(intermediateRange, newRange, increased: false);
            await secondReadGate.WaitUntilBlockedAsync(cancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            secondReadGate.Release();
            await Task.WhenAll(secondRangeChangeTask, reconciliationTask).WaitAsync(cancellation.Token);
            Assert.False(firstRangeChangeTask.IsCompleted);

            firstReadGate.Release();
            await firstRangeChangeTask.WaitAsync(cancellation.Token);
        }
        finally
        {
            firstReadGate.Release();
            secondReadGate?.Release();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_FollowsLatestRefresh()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var obsoleteRead = reminderTable.BlockNextRangeRead(cancellation.Token);
        RangeReadGate? currentRead = null;
        try
        {
            var obsoleteRangeChange = reminderService.TestOnlyChangeRange(oldRange, newRange, increased: false);
            await obsoleteRead.WaitUntilBlockedAsync(cancellation.Token);
            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);

            currentRead = reminderTable.BlockNextRangeRead(cancellation.Token);
            var currentRefresh = reminderService.TestOnlyRefresh();
            await currentRead.WaitUntilBlockedAsync(cancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            currentRead.Release();
            await Task.WhenAll(currentRefresh, reconciliationTask).WaitAsync(cancellation.Token);
            Assert.False(obsoleteRangeChange.IsCompleted);

            obsoleteRead.Release();
            await obsoleteRangeChange.WaitAsync(cancellation.Token);
        }
        finally
        {
            obsoleteRead.Release();
            currentRead?.Release();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RefreshStartBarrier_CompletesBeforeReconciliation()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var readGate = reminderTable.BlockNextRangeRead(cancellation.Token);
        var schedulerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScheduler = new ManualResetEventSlim();
        var blockingTask = new Task(() =>
        {
            schedulerBlocked.TrySetResult();
            releaseScheduler.Wait(cancellation.Token);
        });
        reminderService.Scheduler.QueueTask(blockingTask);
        await schedulerBlocked.Task.WaitAsync(cancellation.Token);
        try
        {
            var refreshStarted = reminderService.TestOnlyStartRefresh();
            var refresh = await refreshStarted.WaitAsync(cancellation.Token);
            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);
            Assert.False(refresh.IsCompleted);
            Assert.False(reconciliationTask.IsCompleted);

            releaseScheduler.Set();
            await blockingTask.WaitAsync(cancellation.Token);
            await readGate.WaitUntilBlockedAsync(cancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            readGate.Release();
            await Task.WhenAll(refresh, reconciliationTask).WaitAsync(cancellation.Token);
        }
        finally
        {
            releaseScheduler.Set();
            readGate.Release();
            await blockingTask.WaitAsync(cancellation.Token);
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_PreservesRefreshThenRangeOrder()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var schedulerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScheduler = new ManualResetEventSlim();
        var blockingTask = new Task(() =>
        {
            schedulerBlocked.TrySetResult();
            releaseScheduler.Wait(cancellation.Token);
        });
        reminderService.Scheduler.QueueTask(blockingTask);
        await schedulerBlocked.Task.WaitAsync(cancellation.Token);

        var refreshRead = reminderTable.BlockNextRangeRead(cancellation.Token);
        RangeReadGate? rangeChangeRead = null;
        try
        {
            var refresh = reminderService.TestOnlyRefresh();
            var rangeChange = reminderService.TestOnlyChangeRange(oldRange, newRange, increased: false);
            var reconciliation = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);
            Assert.False(reconciliation.IsCompleted);

            releaseScheduler.Set();
            await blockingTask.WaitAsync(cancellation.Token);
            await refreshRead.WaitUntilBlockedAsync(cancellation.Token);

            rangeChangeRead = reminderTable.BlockNextRangeRead(cancellation.Token);
            refreshRead.Release();
            await rangeChangeRead.WaitUntilBlockedAsync(cancellation.Token);
            Assert.False(reconciliation.IsCompleted);

            rangeChangeRead.Release();
            await Task.WhenAll(refresh, rangeChange, reconciliation).WaitAsync(cancellation.Token);
        }
        finally
        {
            releaseScheduler.Set();
            refreshRead.Release();
            rangeChangeRead?.Release();
            await blockingTask.WaitAsync(cancellation.Token);
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_PropagatesCurrentProviderReadFailure()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var readGate = reminderTable.BlockNextRangeRead(cancellation.Token);
        var failure = new ControlledRangeReadException();
        try
        {
            var rangeChangeTask = reminderService.TestOnlyChangeRange(oldRange, newRange, increased: false);
            await readGate.WaitUntilBlockedAsync(cancellation.Token);
            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);

            readGate.Fail(failure);

            var rangeChangeException = await Assert.ThrowsAsync<ControlledRangeReadException>(
                () => rangeChangeTask.WaitAsync(cancellation.Token));
            var reconciliationException = await Assert.ThrowsAsync<ControlledRangeReadException>(
                () => reconciliationTask.WaitAsync(cancellation.Token));
            Assert.Same(failure, rangeChangeException);
            Assert.Same(failure, reconciliationException);

            await reminderService.TestOnlyChangeRange(newRange, oldRange, increased: true)
                .WaitAsync(cancellation.Token);
            await reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);
        }
        finally
        {
            readGate.Release();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_IgnoresObsoleteProviderReadFailure()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var intermediateRange = RangeFactory.CreateRange(0, uint.MaxValue / 3);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var obsoleteRead = reminderTable.BlockNextRangeRead(cancellation.Token);
        RangeReadGate? currentRead = null;
        var failure = new ControlledRangeReadException();
        try
        {
            var obsoleteRangeChange = reminderService.TestOnlyChangeRange(oldRange, intermediateRange, increased: false);
            await obsoleteRead.WaitUntilBlockedAsync(cancellation.Token);
            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);

            currentRead = reminderTable.BlockNextRangeRead(cancellation.Token);
            var currentRangeChange = reminderService.TestOnlyChangeRange(intermediateRange, newRange, increased: false);
            await currentRead.WaitUntilBlockedAsync(cancellation.Token);
            currentRead.Release();
            await Task.WhenAll(currentRangeChange, reconciliationTask).WaitAsync(cancellation.Token);

            obsoleteRead.Fail(failure);
            var obsoleteException = await Assert.ThrowsAsync<ControlledRangeReadException>(
                () => obsoleteRangeChange.WaitAsync(cancellation.Token));
            Assert.Same(failure, obsoleteException);
            Assert.True(reconciliationTask.IsCompletedSuccessfully);
        }
        finally
        {
            obsoleteRead.Release();
            currentRead?.Release();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_StopsWaitingWhenCanceled()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var readGate = reminderTable.BlockNextRangeRead(cancellation.Token);
        try
        {
            var rangeChangeTask = reminderService.TestOnlyChangeRange(oldRange, newRange, increased: false);
            await readGate.WaitUntilBlockedAsync(cancellation.Token);

            using var barrierCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(barrierCancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            barrierCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciliationTask);

            readGate.Release();
            await rangeChangeTask.WaitAsync(cancellation.Token);
        }
        finally
        {
            readGate.Release();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_DoesNotDependOnServiceSchedulerAvailability()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        _ = await fixture.ReminderHarness.WaitForServicesReadyAsync(
            [silo],
            TestConstants.InitTimeout,
            cancellation.Token);

        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        await reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);

        var schedulerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScheduler = new ManualResetEventSlim();
        var blockingTask = new Task(() =>
        {
            schedulerBlocked.TrySetResult();
            releaseScheduler.Wait(cancellation.Token);
        });
        reminderService.Scheduler.QueueTask(blockingTask);
        await schedulerBlocked.Task.WaitAsync(cancellation.Token);

        try
        {
            using var barrierCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            barrierCancellation.CancelAfter(TimeSpan.FromSeconds(1));
            await reminderService.TestOnlyWaitForRangeChangeReconciliation(barrierCancellation.Token);
        }
        finally
        {
            releaseScheduler.Set();
            await blockingTask.WaitAsync(cancellation.Token);
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task StartupTopologyBarrier_UsesCompletedInitialLoadForSingleSilo()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
            fixture.HostedCluster,
            fixture.DiagnosticObserver,
            [silo],
            cancellation.Token);

        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var schedulerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScheduler = new ManualResetEventSlim();
        var blockingTask = new Task(() =>
        {
            schedulerBlocked.TrySetResult();
            releaseScheduler.Wait(cancellation.Token);
        });
        reminderService.Scheduler.QueueTask(blockingTask);
        await schedulerBlocked.Task.WaitAsync(cancellation.Token);

        try
        {
            await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
                fixture.HostedCluster,
                fixture.DiagnosticObserver,
                [silo],
                cancellation.Token);
        }
        finally
        {
            releaseScheduler.Set();
            await blockingTask.WaitAsync(cancellation.Token);
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task StableTopologyBarrier_DoesNotStartAnExtraRefresh()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
            fixture.HostedCluster,
            fixture.DiagnosticObserver,
            [silo],
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var rangeReadCount = reminderTable.RangeReadCount;

        await ReminderTopologyStabilizer.WaitForStableTopologyAsync(
            fixture.HostedCluster,
            fixture.DiagnosticObserver,
            [silo],
            cancellation.Token);

        Assert.Equal(rangeReadCount, reminderTable.RangeReadCount);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task StartupTopologyBarrier_DoesNotRefreshStableMultiSiloTopology()
    {
        var builder = new InProcessTestClusterBuilder(2);
        var clock = builder.AddReminderTestClock();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.AddReminders();
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<NullReturningReminderTable>();
                services.AddSingleton<IReminderTable>(
                    static provider => provider.GetRequiredService<NullReturningReminderTable>());
            });
        });

        var cluster = builder.Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        try
        {
            await cluster.DeployAsync(cancellation.Token);
            await ReminderTopologyStabilizer.WaitForReconciledTopologyAsync(
                cluster,
                clock.DiagnosticObserver,
                cluster.Silos,
                cancellation.Token);
            var reminderTables = cluster.Silos
                .Select(silo => silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>())
                .ToArray();
            var rangeReadCounts = reminderTables.Select(table => table.RangeReadCount).ToArray();

            await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
                cluster,
                clock.DiagnosticObserver,
                cluster.Silos,
                cancellation.Token);

            Assert.Equal(rangeReadCounts, reminderTables.Select(table => table.RangeReadCount));
        }
        finally
        {
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                await cluster.StopAllSilosAsync(cleanupCancellation.Token);
            }
            finally
            {
                try
                {
                    using var disposeCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                    await cluster.DisposeAsync().AsTask().WaitAsync(disposeCancellation.Token);
                }
                finally
                {
                    clock.Dispose();
                }
            }
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task StableTopologyBarrier_WaitsForMembershipRangeReconciliation()
    {
        var initialSilo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
            fixture.HostedCluster,
            fixture.DiagnosticObserver,
            [initialSilo],
            cancellation.Token);

        var reminderTable = initialSilo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = initialSilo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var rangeRead = reminderTable.BlockNextRangeRead(cancellation.Token);
        var schedulerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScheduler = new ManualResetEventSlim();
        var blockingTask = new Task(() =>
        {
            schedulerBlocked.TrySetResult();
            releaseScheduler.Wait(cancellation.Token);
        });
        reminderService.Scheduler.QueueTask(blockingTask);
        await schedulerBlocked.Task.WaitAsync(cancellation.Token);
        InProcessSiloHandle? joinedSilo = null;
        try
        {
            joinedSilo = Assert.Single(await fixture.HostedCluster.StartSilosAsync(1, cancellation.Token));
            await reminderService.TestOnlyWaitForSiloStatusListeners(cancellation.Token);

            var barrier = ReminderTopologyStabilizer.WaitForStableTopologyAsync(
                fixture.HostedCluster,
                fixture.DiagnosticObserver,
                [joinedSilo],
                cancellation.Token);
            Assert.False(barrier.IsCompleted);

            releaseScheduler.Set();
            await blockingTask.WaitAsync(cancellation.Token);
            await rangeRead.WaitUntilBlockedAsync(cancellation.Token);
            Assert.False(barrier.IsCompleted);

            rangeRead.Release();
            await barrier;
        }
        finally
        {
            releaseScheduler.Set();
            rangeRead.Release();
            await blockingTask.WaitAsync(cancellation.Token);
            if (joinedSilo is not null)
            {
                using var siloCleanup = new CancellationTokenSource(TestConstants.InitTimeout);
                await fixture.HostedCluster.StopSiloAsync(joinedSilo, siloCleanup.Token);
                await fixture.HostedCluster.WaitForLivenessToStabilizeAsync();
            }
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReconciledTopologyBarrier_WaitsForQueuedRangeChange()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
            fixture.HostedCluster,
            fixture.DiagnosticObserver,
            [silo],
            cancellation.Token);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var readGate = reminderTable.BlockNextRangeRead(cancellation.Token);

        try
        {
            var rangeChange = reminderService.TestOnlyChangeRange(oldRange, newRange, increased: false);
            var barrier = ReminderTopologyStabilizer.WaitForReconciledTopologyAsync(
                fixture.HostedCluster,
                fixture.DiagnosticObserver,
                [silo],
                cancellation.Token);

            await readGate.WaitUntilBlockedAsync(cancellation.Token);
            Assert.False(barrier.IsCompleted);

            readGate.Release();
            await Task.WhenAll(rangeChange, barrier).WaitAsync(cancellation.Token);
        }
        finally
        {
            readGate.Release();
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task ReconciledTopologyBarrier_ObservesStartedServiceWithLateObserver()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.InitTimeout);
        await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
            fixture.HostedCluster,
            fixture.DiagnosticObserver,
            [silo],
            cancellation.Token);

        using var lateObserver = ReminderDiagnosticObserver.Create(fixture.HostedCluster);
        await ReminderTopologyStabilizer.WaitForReconciledTopologyAsync(
            fixture.HostedCluster,
            lateObserver,
            [silo],
            cancellation.Token);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        private ReminderTestClock? _clock;

        public ReminderLifecycleHarness ReminderHarness { get; } = new();
        public ReminderDiagnosticObserver DiagnosticObserver { get; private set; } = null!;

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            _clock = builder.AddReminderTestClock();
            DiagnosticObserver = _clock.DiagnosticObserver;
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.AddReminders();
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<NullReturningReminderTable>();
                    services.AddSingleton<IReminderTable>(
                        static provider => provider.GetRequiredService<NullReturningReminderTable>());
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                await base.DisposeAsync().AsTask().WaitAsync(cleanupCancellation.Token);
            }
            finally
            {
                ReminderHarness.Dispose();
                _clock?.Dispose();
            }
        }
    }

    private sealed class BlockingSiloStatusListener(SiloAddress localSilo) : ISiloStatusListener, IDisposable
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _hasBlocked;

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (updatedSilo.Equals(localSilo)
                || status != SiloStatus.Active
                || Interlocked.Exchange(ref _hasBlocked, 1) != 0)
            {
                return;
            }

            _blocked.TrySetResult();
            _release.Wait();
        }

        public Task WaitUntilBlockedAsync(CancellationToken cancellationToken)
            => _blocked.Task.WaitAsync(cancellationToken);

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class NullReturningReminderTable : IReminderTable
    {
        private int rangeReadCount;
        private RangeReadGate? nextRangeRead;

        public NullReturningReminderTable()
        {
        }

        public NullReturningReminderTable(RangeReadGate? initialRead)
        {
            nextRangeRead = initialRead;
        }

        public int RangeReadCount => Volatile.Read(ref rangeReadCount);

        public RangeReadGate BlockNextRangeRead(CancellationToken cancellationToken)
        {
            var gate = new RangeReadGate(cancellationToken);
            if (Interlocked.CompareExchange(ref nextRangeRead, gate, null) is not null)
            {
                throw new InvalidOperationException("A range read is already blocked.");
            }

            return gate;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            Interlocked.Increment(ref rangeReadCount);
            if (Interlocked.Exchange(ref nextRangeRead, null) is { } gate)
            {
                gate.MarkBlocked();
                return gate.Task;
            }

            // Simulate a provider binary compiled before the return value was annotated as non-null.
            return Task.FromResult<ReminderTableData>(null!);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string?> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class RangeReadGate
    {
        private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ReminderTableData> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RangeReadGate(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => release.TrySetCanceled(cancellationToken));
        }

        public Task<ReminderTableData> Task => release.Task;

        public void MarkBlocked() => blocked.TrySetResult();

        public Task WaitUntilBlockedAsync(CancellationToken cancellationToken) => blocked.Task.WaitAsync(cancellationToken);

        public void Release() => release.TrySetResult(null!);

        public void Fail(Exception exception) => release.TrySetException(exception);
    }

    private sealed class ControlledRangeReadException : Exception;
}
