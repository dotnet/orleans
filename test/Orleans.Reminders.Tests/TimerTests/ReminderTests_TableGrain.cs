#nullable enable

//#define USE_SQL_SERVER

using Microsoft.Extensions.DependencyInjection;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Internal;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace UnitTests.TimerTests
{
    /// <summary>
    /// Tests for grain-based reminder functionality using in-memory reminder service as table storage.
    /// </summary>
    [TestCategory("Functional"), TestCategory("Reminders")]
    public class ReminderTests_TableGrain : ReminderTestsBase, IClassFixture<ReminderTests_TableGrain.Fixture>
    {
        private static readonly TimeSpan ReminderLoadingWindow = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan ReminderRefreshPeriod = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumInitialRefreshStagger = TimeSpan.FromSeconds(31);

        public class Fixture : BaseInProcessTestClusterFixture
        {
            private ReminderTestClock? _reminderClock;
            internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");
            internal ReminderTableReadController ReadController { get; } = new();

            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
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

            public override async Task DisposeAsync()
            {
                try
                {
                    await base.DisposeAsync();
                }
                finally
                {
                    _reminderClock?.Dispose();
                }
            }
        }

        public ReminderTests_TableGrain(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
        {
            _readController = fixture.ReadController;
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            controlProxy.EraseReminderTable().WaitAsync(TestConstants.InitTimeout).Wait();
        }

        private readonly ReminderTableReadController _readController;

        // Basic tests

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
        [Fact(Skip = "https://github.com/dotnet/orleans/issues/9555")]
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
            await AdvanceReminderTimeAsync(MaximumInitialRefreshStagger, cts.Token);
            await activatedTask;
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);

            var firstTickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            var firstEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceReminderTimeAsync(dueTime - MaximumInitialRefreshStagger, cts.Token);
            await firstTickTask;
            await firstEvictionTask;

            Assert.NotNull(await grain.GetReminderObject(reminderName));

            var reactivatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceReminderTimeAsync(period - ReminderLoadingWindow + ReminderRefreshPeriod, cts.Token);
            await reactivatedTask;

            var secondTickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            var secondEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceReminderTimeAsync(ReminderLoadingWindow - ReminderRefreshPeriod, cts.Token);
            await secondTickTask;
            await secondEvictionTask;

            await grain.StopReminder(reminderName);
            Assert.Null(await grain.GetReminderObject(reminderName));
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
            while (!blockedTask.IsCompleted)
            {
                await AdvanceReminderTimeAsync(ReminderRefreshPeriod, cancellationToken);
                await Task.WhenAny(blockedTask, Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken));
            }

            await blockedTask;
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
