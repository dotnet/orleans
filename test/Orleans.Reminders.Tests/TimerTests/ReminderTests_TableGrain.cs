#nullable enable

//#define USE_SQL_SERVER

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Internal;
using Orleans.Reminders;
using Orleans.Runtime.ReminderService;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    /// <summary>
    /// Tests for grain-based reminder functionality using in-memory reminder service as table storage.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Reminders")]
    [TestCategory("Functional"), TestCategory("Reminders")]
    public class ReminderTests_TableGrain : ReminderTestsBase, IClassFixture<ReminderTests_TableGrain.Fixture>, IAsyncLifetime
    {
        private static readonly TimeSpan ReminderLoadingWindow = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan ReminderRefreshPeriod = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumInitialRefreshStagger = TimeSpan.FromSeconds(31);

        public class Fixture : BaseInProcessTestClusterFixture
        {
            private ReminderTestClock? _reminderClock;
            private readonly ReminderDiagnosticObserver _startupObserver = ReminderDiagnosticObserver.Create();
            private IReadOnlyList<SiloAddress>? _startedReminderServices;
            internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");
            internal ReminderTableReadController ReadController { get; } = new();
            internal IReadOnlyList<SiloAddress> StartedReminderServices => _startedReminderServices
                ?? throw new InvalidOperationException("Reminder services have not completed startup.");

            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
                // The controlled-read tests validate ordering within one reminder owner.
                builder.Options.InitialSilosCount = 1;
                _reminderClock = builder.AddReminderTestClock();
                builder.ConfigureSilo((_, siloBuilder) =>
                {
                    siloBuilder.Configure<ReminderOptions>(options => options.ReminderLoadingWindow = ReminderLoadingWindow)
                        .AddMemoryGrainStorageAsDefault()
                        .AddReminders()
                        .UseInMemoryReminderService()
                        .ConfigureServices(services =>
                        {
                            services.AddSingleton(ReadController);
                            services.AddSingleton<ControllableReminderTable>();
                            services.AddSingleton<IReminderTable>(serviceProvider => serviceProvider.GetRequiredService<ControllableReminderTable>());
                        });
                });
            }

            public override async Task InitializeAsync()
            {
                await base.InitializeAsync();

                using var cancellation = new CancellationTokenSource(TestConstants.InitTimeout);
                var started = HostedCluster.Silos.Select(silo =>
                    _startupObserver.WaitForReminderServiceStartedAsync(cancellation.Token, silo.SiloAddress));
                _startedReminderServices = (await Task.WhenAll(started)).Select(e => e.SiloAddress!).ToArray();
            }

            public override async Task DisposeAsync()
            {
                try
                {
                    await base.DisposeAsync();
                }
                finally
                {
                    _reminderClock?.Dispose();
                    _startupObserver.Dispose();
                }
            }
        }

        public ReminderTests_TableGrain(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
        {
            _fixture = fixture;
            _readController = fixture.ReadController;
        }

        private readonly Fixture _fixture;
        private readonly ReminderTableReadController _readController;

        public async Task InitializeAsync()
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout);
        }

        Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

        // Basic tests

        [Fact]
        public void Fixture_WaitsForReminderServicesToStart()
        {
            Assert.All(
                HostedCluster.Silos,
                silo => Assert.Contains(silo.SiloAddress, _fixture.StartedReminderServices));
        }

        [Fact]
        public void ReminderTestClock_IsScopedToReminderService()
        {
            foreach (var silo in HostedCluster.Silos)
            {
                var unkeyedProvider = silo.ServiceProvider.GetRequiredService<TimeProvider>();
                var reminderProvider = silo.ServiceProvider.GetRequiredKeyedService<TimeProvider>(ReminderTimeProviderNames.Reminders);

                Assert.Same(TimeProvider.System, unkeyedProvider);
                Assert.NotSame(unkeyedProvider, reminderProvider);
            }
        }

        /// <summary>
        /// Tests basic reminder operations including stopping reminders by reference.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        /// <summary>
        /// Tests basic reminder list operations including creation and retrieval.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        /// <summary>
        /// Tests handling of multiple reminders per grain.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_MultipleReminders()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }

        [Fact]
        public async Task Rem_Grain_UpdateReminder_DoesNotRestartLocalReminder()
        {
            await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder();
        }

        [Fact]
        public async Task Rem_Grain_ConcurrentCounterWaitersUseSingleClockDriver()
        {
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);
            var grains = Enumerable.Range(0, 16)
                .Select(_ => this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid()))
                .ToArray();

            await Task.WhenAll(grains.Select(grain => grain.StartReminder(DR)));
            await AdvanceRemindersByTicksAsync(1, cts.Token, GetReminderIdentities(grains, DR));
            await AssertReminderCountersAsync(grains, (DR, 1));
            await StopRemindersAsync(grains, DR, cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_CanRestartBeforeRemovedReminderIsPurged()
        {
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();

            await grain.StartReminder(DR);
            await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 1, cts.Token);

            var unregisteredTask = observer.WaitForReminderUnregisteredAsync(grainId, DR, cts.Token);
            await grain.StopReminder(DR);
            await unregisteredTask;

            await grain.StartReminder(DR);
            var restartedCount = await WaitForAdditionalReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 1, cts.Token);
            Assert.Equal(1, restartedCount);

            await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_DistantReminder_IsLoadedWithinWindowAndEvictedAfterFiring()
        {
            const string reminderName = "distant_reminder";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + TimeSpan.FromSeconds(5);
            var period = ReminderLoadingWindow + TimeSpan.FromSeconds(10);
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            var registeredTask = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
            var reminder = await grain.StartReminder(reminderName, dueTime, period);
            await registeredTask;

            var storedReminder = await grain.GetReminderObject(reminderName);
            Assert.NotNull(storedReminder);
            Assert.Equal(reminder.ReminderName, storedReminder.ReminderName);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(0, observer.GetTickCount(grainId, reminderName));

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceUntilAsync(activatedTask, cts.Token);
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var firstTickTask = observer.WaitForTickCountAsync(grainId, 1, cts.Token, reminderName);
            var firstEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceUntilAsync(firstTickTask, cts.Token);
            await firstEvictionTask;

            Assert.NotNull(await grain.GetReminderObject(reminderName));

            var reactivatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceUntilAsync(reactivatedTask, cts.Token);

            var secondTickTask = observer.WaitForTickCountAsync(grainId, 2, cts.Token, reminderName);
            var secondEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceUntilAsync(secondTickTask, cts.Token);
            await secondEvictionTask;

            await grain.StopReminder(reminderName);
            Assert.Null(await grain.GetReminderObject(reminderName));
        }

        [Fact]
        public async Task Rem_Grain_RefreshAtFiredTickDoesNotRedeliverOccurrence()
        {
            const string reminderName = "stale_refresh_at_fired_tick";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + TimeSpan.FromSeconds(5);
            var period = ReminderLoadingWindow + TimeSpan.FromSeconds(10);
            var firstTickTime = ReminderUtcNow.UtcDateTime + dueTime;
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, period);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceUntilAsync(activatedTask, cts.Token);
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var firstTickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            var firstEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceReminderTimeAsync(firstTickTime - ReminderUtcNow.UtcDateTime, cts.Token);
            var firstTick = await firstTickTask;
            await firstEvictionTask;

            Assert.Equal(firstTickTime, firstTick.Status.CurrentTickTime);
            Assert.Equal(1, observer.GetTickCount(grainId, reminderName));

            var reminderService = HostedCluster.Silos.Single().ServiceProvider.GetRequiredService<LocalReminderService>();
            await reminderService.TestOnlyRefresh();

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(1, observer.GetTickCount(grainId, reminderName));

            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Rem_Grain_DirectUpdatesMoveReminderInAndOutOfLoadingWindow()
        {
            const string reminderName = "updated_reminder";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
            await activatedTask;

            var evictedTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
            await evictedTask;

            Assert.NotNull(await grain.GetReminderObject(reminderName));
            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotReloadNearScheduleAfterDistantUpdate()
        {
            const string reminderName = "stale_refresh_distant_update";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2));
            await activatedTask;

            await using var staleRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
            await quiescenceTask;
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            followingRead.Release();
            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotRestoreUnregisteredReminder()
        {
            const string reminderName = "stale_refresh_unregister";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2));
            await activatedTask;

            await using var staleRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StopReminder(reminderName);
            await quiescenceTask;
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Null(await grain.GetReminderObject(reminderName));
            followingRead.Release();
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotReloadStorageOnlyScheduleAfterDistantUpdate()
        {
            const string reminderName = "stale_refresh_storage_only_update";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + MaximumInitialRefreshStagger + TimeSpan.FromSeconds(5);
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, TimeSpan.FromMinutes(2));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var staleRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            await grain.StartReminder(reminderName, dueTime + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
            await AdvanceReminderTimeAsync(TimeSpan.FromSeconds(6), cts.Token);
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            followingRead.Release();
            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotRestoreUnregisteredStorageOnlyReminder()
        {
            const string reminderName = "stale_refresh_storage_only_unregister";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + MaximumInitialRefreshStagger + TimeSpan.FromSeconds(5);
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, TimeSpan.FromMinutes(2));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var staleRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            await grain.StopReminder(reminderName);
            await AdvanceReminderTimeAsync(TimeSpan.FromSeconds(6), cts.Token);
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Null(await grain.GetReminderObject(reminderName));
            followingRead.Release();
        }

        [Fact]
        public async Task Rem_Grain_NearUpdateReplacesReminderPendingRemoval()
        {
            const string reminderName = "replace_pending_removal";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            var initialActivation = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2));
            await initialActivation;

            await using var staleRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
            await quiescenceTask;

            var replacementActivation = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2));
            await replacementActivation;
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(1, observer.GetActiveReminderCount(grainId, reminderName));
            followingRead.Release();
            await grain.StopReminder(reminderName);
        }

        private async Task AdvanceUntilBlockedAsync(ReminderTableReadGate gate, CancellationToken cancellationToken)
        {
            var blockedTask = gate.WaitUntilBlockedAsync(cancellationToken);
            await AdvanceUntilAsync(blockedTask, cancellationToken);
        }

        private async Task AdvanceUntilAsync(Task task, CancellationToken cancellationToken)
        {
            while (!task.IsCompleted)
            {
                await AdvanceReminderTimeAsync(ReminderRefreshPeriod, cancellationToken);
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken));
            }

            await task;
        }

        [Fact]
        public async Task Rem_Grain_DueTimeAndPeriodBeyondTimerLimit_RemainStorageOnly()
        {
            const string reminderName = "very_distant_reminder";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var unsupportedTimerDelay = TimeSpan.FromMilliseconds(0xfffffffe) + TimeSpan.FromMilliseconds(1);

            await grain.StartReminder(reminderName, unsupportedTimerDelay, unsupportedTimerDelay);

            Assert.NotNull(await grain.GetReminderObject(reminderName));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await grain.StopReminder(reminderName);
            Assert.Null(await grain.GetReminderObject(reminderName));
        }

        [Fact]
        public async Task Rem_Grain_DueTimeAndPeriodBeyondTimerLimit_LoadsWithinWindowAndFiresOnPersistedCadence()
        {
            const string reminderName = "very_distant_reminder_fires";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var unsupportedTimerDelay = TimeSpan.FromMilliseconds(0xfffffffe) + TimeSpan.FromMilliseconds(1);

            var dueTime = unsupportedTimerDelay + TimeSpan.FromDays(1);
            var period = unsupportedTimerDelay + TimeSpan.FromDays(1);
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            // Subscribe before registration so Skip(1) captures the second tick instead of replaying the first.
            var secondTickTask = ReminderEvents.AllEvents
                .OfType<ReminderEvents.TickCompleted>()
                .Where(e => e.GrainId == grainId && e.ReminderName == reminderName)
                .Skip(1)
                .FirstAsync()
                .ToTask(cts.Token);

            var registeredTask = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, dueTime, period);
            await registeredTask;

            Assert.NotNull(await grain.GetReminderObject(reminderName));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(0, observer.GetTickCount(grainId, reminderName));

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceReminderTimeAsync(dueTime - ReminderLoadingWindow + MaximumInitialRefreshStagger, cts.Token);
            await activatedTask;
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var firstTickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            var firstEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceReminderTimeAsync(ReminderLoadingWindow - MaximumInitialRefreshStagger, cts.Token);
            var firstTick = await firstTickTask;
            await firstEvictionTask;

            Assert.Equal(1, observer.GetTickCount(grainId, reminderName));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.NotNull(await grain.GetReminderObject(reminderName));
            Assert.Equal(firstTick.Status.FirstTickTime, firstTick.Status.CurrentTickTime);

            var reactivatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceReminderTimeAsync(period - ReminderLoadingWindow + ReminderRefreshPeriod, cts.Token);
            await reactivatedTask;
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var secondEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceReminderTimeAsync(ReminderLoadingWindow - ReminderRefreshPeriod, cts.Token);
            var secondTick = await secondTickTask;
            await secondEvictionTask;

            Assert.Equal(2, observer.GetTickCount(grainId, reminderName));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(firstTick.Status.CurrentTickTime + period, secondTick.Status.CurrentTickTime);

            await grain.StopReminder(reminderName);
            Assert.Null(await grain.GetReminderObject(reminderName));
        }

        [Fact]
        public async Task Rem_Grain_StorageRecoveryAtExactDueTime_DeliversDueOccurrence()
        {
            const string reminderName = "exact_due_storage_recovery";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + TimeSpan.FromSeconds(20);
            var period = TimeSpan.FromSeconds(90);
            var firstTickTime = ReminderUtcNow.UtcDateTime + dueTime;
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, period);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var outage = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(outage, cts.Token);
            await AdvanceReminderTimeAsync(firstTickTime - ReminderUtcNow.UtcDateTime, cts.Token);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            outage.Release();
            await activatedTask;
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var tickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            await AdvanceUntilAsync(tickTask, cts.Token);
            var tick = await tickTask;

            Assert.Equal(firstTickTime, tick.Status.FirstTickTime);
            Assert.InRange(tick.Status.CurrentTickTime, firstTickTime, firstTickTime + period - TimeSpan.FromTicks(1));

            await grain.StopReminder(reminderName);
        }

        [Fact]
        public async Task Rem_Grain_DistantReminder_DeliveredLateAfterStorageOutageRecovers()
        {
            const string reminderName = "storage_outage_recovery";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + TimeSpan.FromSeconds(20);
            var period = TimeSpan.FromSeconds(90);
            var firstTickTime = ReminderUtcNow.UtcDateTime + dueTime;
            using var cts = new CancellationTokenSource(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, period);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var outage = _readController.BlockNextRangeRead(grainId);
            await AdvanceUntilBlockedAsync(outage, cts.Token);

            await AdvanceReminderTimeAsync(dueTime + TimeSpan.FromSeconds(10), cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(0, observer.GetTickCount(grainId, reminderName));
            Assert.NotNull(await grain.GetReminderObject(reminderName));

            outage.Release();

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceUntilAsync(activatedTask, cts.Token);
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var tickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            var expectedTickTime = firstTickTime + period;
            var timeUntilTick = expectedTickTime - ReminderUtcNow.UtcDateTime;
            Assert.True(timeUntilTick > TimeSpan.Zero, $"Expected the recovered reminder to be armed before {expectedTickTime:O}, but reminder time was {ReminderUtcNow:O}.");
            await AdvanceReminderTimeAsync(timeUntilTick, cts.Token);
            var tick = await tickTask;

            Assert.Equal(1, observer.GetTickCount(grainId, reminderName));
            Assert.Equal(expectedTickTime, tick.Status.CurrentTickTime);

            await grain.StopReminder(reminderName);
        }

        // Single join tests ... multi grain, multi reminders

        /// <summary>
        /// Tests single join scenario with multiple grains and multiple reminders.
        /// </summary>
        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4318")]
        public async Task Rem_Grain_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        /// <summary>
        /// Tests handling of reminder not found scenarios.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_ReminderNotFounds()
        {
            await Test_Reminders_ReminderNotFound();
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
