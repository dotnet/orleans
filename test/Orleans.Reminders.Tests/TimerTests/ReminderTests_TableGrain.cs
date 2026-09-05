#nullable enable

//#define USE_SQL_SERVER

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Internal;
using Orleans.Reminders;
using Orleans.Runtime;
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
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Reminders")]
    [TestCategory("Functional"), TestCategory("Reminders")]
    public sealed class ReminderServiceLifecycleTests_TableGrain
        : ReminderServiceLifecycleTestsBase, IClassFixture<ReminderTests_TableGrain.Fixture>
    {
        public ReminderServiceLifecycleTests_TableGrain(ReminderTests_TableGrain.Fixture fixture)
            : base(fixture.ReminderClock, fixture.HostedCluster, "InMemory")
        {
        }
    }

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
            private IReadOnlyList<SiloAddress>? _startedReminderServices;
            internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");
            internal ReminderTableReadController ReadController { get; } = new();
            internal IReadOnlyList<SiloAddress> StartedReminderServices => _startedReminderServices
                ?? throw new InvalidOperationException("Reminder services have not reached startup topology.");

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

            public override async ValueTask InitializeAsync()
            {
                await base.InitializeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
                if (!PreconditionsMet)
                {
                    return;
                }

                using var topologyCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                topologyCancellation.CancelAfter(TestConstants.InitTimeout);
                try
                {
                    var startedSilos = await ReminderTopologyStabilizer.WaitForStartupTopologyAsync(
                        HostedCluster,
                        ReminderClock.DiagnosticObserver,
                        HostedCluster.Silos,
                        topologyCancellation.Token);
                    _startedReminderServices = startedSilos.Select(static silo => silo.SiloAddress).ToArray();
                }
                catch (OperationCanceledException exception) when (
                    topologyCancellation.IsCancellationRequested
                    && !TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Reminder startup topology stabilization timed out within {TestConstants.InitTimeout}. Diagnostics: {exception.Message}",
                        exception);
                }
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
                    _reminderClock?.Dispose();
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

        public async ValueTask InitializeAsync()
        {
            await ClearReminderTableAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestConstants.InitTimeout, TestContext.Current.CancellationToken);
        }

        // Basic tests

        [Fact]
        public async Task Fixture_WaitsForReminderStartupTopologyBeforeGrainCalls()
        {
            Assert.All(
                HostedCluster.Silos,
                silo => Assert.Contains(silo.SiloAddress, _fixture.StartedReminderServices));

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TestConstants.InitTimeout);
            await ClearReminderTableAsync(cancellation.Token);
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
            await Test_Reminders_Basic_StopByRef(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic reminder list operations including creation and retrieval.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests handling of multiple reminders per grain.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_MultipleReminders()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Rem_Grain_UpdateReminder_DoesNotRestartLocalReminder()
        {
            await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Rem_Grain_ConcurrentRemindersUseSingleClockDriver()
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);
            var grains = Enumerable.Range(0, 16)
                .Select(_ => this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid()))
                .ToArray();

            await Task.WhenAll(grains.Select(grain => grain.StartReminder(DR))).WaitAsync(cts.Token);
            await AdvanceRemindersByTicksAsync(1, cts.Token, GetReminderIdentities(grains, DR));
            await AssertReminderCountersAsync(grains, cts.Token, (DR, 1));
            await StopRemindersAsync(grains, DR, cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_TickCompletionSurvivesActivationReplacement()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TestConstants.InitTimeout);
            var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();

            await grain.StartReminder(DR).WaitAsync(cancellation.Token);
            await observer.WaitForSchedulesArmedAsync(cancellation.Token, (grain, DR));
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => grain.GetCounter(DR).WaitAsync(cancellation.Token));
            var period = await grain.GetReminderPeriod(DR).WaitAsync(cancellation.Token);

            var tickCompletion = observer.ArmTickCompletion(cancellation.Token, (grain, DR));
            await HostedCluster.DeactivateAsync(grainId).WaitAsync(cancellation.Token);
            Assert.False(HostedCluster.TryGetGrainContext(grainId, out _));

            await AdvanceReminderTimeAsync(period, cancellation.Token);
            await observer.WaitForTickCompletedAsync(tickCompletion).WaitAsync(cancellation.Token);

            Assert.Equal(1, await grain.GetCounter(DR).WaitAsync(cancellation.Token));
            Assert.Equal(1, observer.GetTickCount(grainId, DR));
            await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cancellation.Token);
        }

        [Fact]
        public async Task Rem_Grain_GT_1F1J_MultiGrain()
        {
            await Test_Reminders_GT_1F1J_MultiGrain(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Rem_Grain_PostTopologyConvergence_DoesNotSuppressMessageRejection()
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);
            var rejection = (OrleansMessageRejectionException)Activator.CreateInstance(
                typeof(OrleansMessageRejectionException),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: ["Controlled post-convergence rejection."],
                culture: null)!;
            var callCounts = new int[4];

            var actual = await Assert.ThrowsAsync<OrleansMessageRejectionException>(() =>
                InvokeGrainCallsAfterTopologyConvergenceAsync(
                    cts.Token,
                    CreateCall(0),
                    CreateCall(1),
                    CreateCall(2, rejection),
                    CreateCall(3)));

            Assert.Same(rejection, actual);
            Assert.All(callCounts, count => Assert.Equal(1, count));

            Func<Task> CreateCall(int index, Exception? exception = null) => () =>
            {
                callCounts[index]++;
                return exception is null ? Task.CompletedTask : Task.FromException(exception);
            };
        }

        [Fact]
        public async Task Rem_Grain_TopologyConvergenceTimeout_PreventsGrainCalls()
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);
            var timeout = new TimeoutException("Controlled topology convergence timeout.");
            var callCount = 0;

            var actual = await Assert.ThrowsAsync<TimeoutException>(() =>
                InvokeGrainCallsAfterTopologyConvergenceAsync(
                    _ => Task.FromException(timeout),
                    cts.Token,
                    () =>
                    {
                        callCount++;
                        return Task.CompletedTask;
                    }));

            Assert.Same(timeout, actual);
            Assert.Equal(0, callCount);
        }

        [Fact]
        public async Task Rem_Grain_CanRestartBeforeRemovedReminderIsPurged()
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();

            await grain.StartReminder(DR).WaitAsync(cts.Token);
            await WaitForReminderCounterAsync(grain, DR, () => grain.GetCounter(DR), 1, cts.Token);

            var unregisteredTask = observer.WaitForReminderUnregisteredAsync(grainId, DR, cts.Token);
            await grain.StopReminder(DR).WaitAsync(cts.Token);
            await unregisteredTask;

            await grain.StartReminder(DR).WaitAsync(cts.Token);
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            var registeredTask = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
            var reminder = await grain.StartReminder(reminderName, dueTime, period).WaitAsync(cts.Token);
            await registeredTask;

            var storedReminder = await grain.GetReminderObject(reminderName).WaitAsync(cts.Token);
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

            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));

            var reactivatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await AdvanceUntilAsync(reactivatedTask, cts.Token);

            var secondTickTask = observer.WaitForTickCountAsync(grainId, 2, cts.Token, reminderName);
            var secondEvictionTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await AdvanceUntilAsync(secondTickTask, cts.Token);
            await secondEvictionTask;

            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
            Assert.Null(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, period).WaitAsync(cts.Token);

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

            await observer.RefreshActiveServicesAsync(cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(1, observer.GetTickCount(grainId, reminderName));

            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_DirectUpdatesMoveReminderInAndOutOfLoadingWindow()
        {
            const string reminderName = "updated_reminder";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await activatedTask;

            var evictedTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await evictedTask;

            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotReloadNearScheduleAfterDistantUpdate()
        {
            const string reminderName = "stale_refresh_distant_update";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await activatedTask;

            await using var staleRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await quiescenceTask;
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            followingRead.Release();
            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotRestoreUnregisteredReminder()
        {
            const string reminderName = "stale_refresh_unregister";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await activatedTask;

            await using var staleRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
            await quiescenceTask;
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Null(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
            followingRead.Release();
        }

        [Fact]
        public async Task Rem_Grain_MissingDiscoveryCandidateRequiresStrongPointAbsenceBeforeRemoval()
        {
            const string reminderName = "missing_discovery_candidate";
            var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var silo = Assert.Single(HostedCluster.Silos);
            var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
            var reminderTable = silo.ServiceProvider.GetRequiredService<ControllableReminderTable>();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TestConstants.InitTimeout);

            var activated = observer.WaitForActiveReminderCountAsync(grainId, 1, cancellation.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2)).WaitAsync(cancellation.Token);
            await activated;

            _readController.OmitFromNextRangeRead(grainId, reminderName);
            await reminderService.TestOnlyRefresh().WaitAsync(cancellation.Token);
            Assert.Equal(1, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cancellation.Token));

            var persisted = Assert.IsType<ReminderEntry>(await reminderTable.ReadRow(grainId, reminderName));
            Assert.True(await reminderTable.RemoveRow(grainId, reminderName, persisted.ETag!));
            var quiesced = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cancellation.Token);
            _readController.OmitFromNextRangeRead(grainId, reminderName);
            await reminderService.TestOnlyRefresh().WaitAsync(cancellation.Token);
            await quiesced;

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Null(await grain.GetReminderObject(reminderName).WaitAsync(cancellation.Token));
        }

        [Fact]
        public async Task RegisterAndUnregisterReconcileThroughPointReadsBeforeReturning()
        {
            const string reminderName = "immediate_point_reconciliation";
            var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var silo = Assert.Single(HostedCluster.Silos);
            var reminderTable = silo.ServiceProvider.GetRequiredService<ControllableReminderTable>();
            var initialPointReads = reminderTable.PointReadCount;

            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2))
                .WaitAsync(TestContext.Current.CancellationToken);
            var afterRegistration = reminderTable.PointReadCount;
            Assert.True(afterRegistration > initialPointReads);

            await grain.StopReminder(reminderName).WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(reminderTable.PointReadCount > afterRegistration);
        }

        [Fact]
        public async Task ConcurrentMutationPointReadsCannotRestoreAnOlderSchedule()
        {
            const string reminderName = "out_of_order_point_reads";
            var grainId = GrainId.Create("point-read-race", Guid.NewGuid().ToString("N"));
            var silo = Assert.Single(HostedCluster.Silos);
            var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TestConstants.InitTimeout);
            await using var olderRead = _readController.BlockNextPointRead(grainId, reminderName, cancellation.Token);

            var olderMutation = reminderService.TestOnlyRegisterOrUpdateReminder(
                grainId,
                reminderName,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2));
            await olderRead.WaitUntilBlockedAsync(cancellation.Token);

            var newerReminder = await reminderService.TestOnlyRegisterOrUpdateReminder(
                grainId,
                reminderName,
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMinutes(4));
            var afterNewerMutation = Assert.IsType<ReminderEntry>(
                await reminderService.TestOnlyGetLocalReminder(grainId, reminderName));
            Assert.Equal(TimeSpan.FromMinutes(4), afterNewerMutation.Period);

            olderRead.Release();
            await olderMutation.WaitAsync(cancellation.Token);
            var afterOlderReadCompletes = Assert.IsType<ReminderEntry>(
                await reminderService.TestOnlyGetLocalReminder(grainId, reminderName));
            Assert.Equal(TimeSpan.FromMinutes(4), afterOlderReadCompletes.Period);

            await reminderService.TestOnlyUnregisterReminder(newerReminder).WaitAsync(cancellation.Token);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotReloadStorageOnlyScheduleAfterDistantUpdate()
        {
            const string reminderName = "stale_refresh_storage_only_update";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + MaximumInitialRefreshStagger + TimeSpan.FromSeconds(5);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var staleRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            await grain.StartReminder(reminderName, dueTime + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await AdvanceReminderTimeAsync(TimeSpan.FromSeconds(6), cts.Token);
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            followingRead.Release();
            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
        }

        [Fact]
        public async Task Rem_Grain_StaleRefreshCannotRestoreUnregisteredStorageOnlyReminder()
        {
            const string reminderName = "stale_refresh_storage_only_unregister";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            var dueTime = ReminderLoadingWindow + MaximumInitialRefreshStagger + TimeSpan.FromSeconds(5);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var staleRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
            await AdvanceReminderTimeAsync(TimeSpan.FromSeconds(6), cts.Token);
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Null(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
            followingRead.Release();
        }

        [Fact]
        public async Task Rem_Grain_NearUpdateReplacesReminderPendingRemoval()
        {
            const string reminderName = "replace_pending_removal";
            var grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var grainId = grain.GetGrainId();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            var initialActivation = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await initialActivation;

            await using var staleRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(staleRead, cts.Token);

            var quiescenceTask = observer.WaitForReminderQuiescenceAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, ReminderLoadingWindow + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await quiescenceTask;

            var replacementActivation = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            await grain.StartReminder(reminderName, ReminderLoadingWindow, TimeSpan.FromMinutes(2)).WaitAsync(cts.Token);
            await replacementActivation;
            staleRead.Release();

            await using var followingRead = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(followingRead, cts.Token);

            Assert.Equal(1, observer.GetActiveReminderCount(grainId, reminderName));
            followingRead.Release();
            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
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

            var cancellationToken = TestContext.Current.CancellationToken;
            await grain.StartReminder(reminderName, unsupportedTimerDelay, unsupportedTimerDelay).WaitAsync(cancellationToken);

            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cancellationToken));
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await grain.StopReminder(reminderName).WaitAsync(cancellationToken);
            Assert.Null(await grain.GetReminderObject(reminderName).WaitAsync(cancellationToken));
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            // Subscribe before registration so Skip(1) captures the second tick instead of replaying the first.
            var secondTickTask = ReminderEvents.AllEvents
                .OfType<ReminderEvents.TickCompleted>()
                .Where(e => e.GrainId == grainId && e.ReminderName == reminderName)
                .Skip(1)
                .FirstAsync()
                .ToTask(cts.Token);

            var registeredTask = observer.WaitForReminderRegisteredAsync(grainId, reminderName, cts.Token);
            await grain.StartReminder(reminderName, dueTime, period).WaitAsync(cts.Token);
            await registeredTask;

            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
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
            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
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

            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
            Assert.Null(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, period).WaitAsync(cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var outage = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(outage, cts.Token);
            await AdvanceReminderTimeAsync(firstTickTime - ReminderUtcNow.UtcDateTime, cts.Token);

            var activatedTask = observer.WaitForActiveReminderCountAsync(grainId, 1, cts.Token, reminderName);
            outage.Release();
            await activatedTask;
            await observer.WaitForLocalReminderScheduleAsync(grainId, reminderName, cts.Token);
            // Complete a reminder-service round trip so the recovery reconciliation turn cannot overlap time advancement.
            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));

            var tickTask = observer.WaitForReminderTickAsync(grainId, cts.Token, reminderName);
            await AdvanceReminderTimeAsync(ReminderRefreshPeriod, cts.Token);
            var tick = await tickTask;

            Assert.Equal(firstTickTime, tick.Status.FirstTickTime);
            Assert.Equal(firstTickTime + ReminderRefreshPeriod, tick.Status.CurrentTickTime);

            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TestConstants.InitTimeout);

            await grain.StartReminder(reminderName, dueTime, period).WaitAsync(cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));

            await using var outage = _readController.BlockNextRangeRead(grainId, cts.Token);
            await AdvanceUntilBlockedAsync(outage, cts.Token);

            await AdvanceReminderTimeAsync(dueTime + TimeSpan.FromSeconds(10), cts.Token);
            Assert.Equal(0, observer.GetActiveReminderCount(grainId, reminderName));
            Assert.Equal(0, observer.GetTickCount(grainId, reminderName));
            Assert.NotNull(await grain.GetReminderObject(reminderName).WaitAsync(cts.Token));

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

            await grain.StopReminder(reminderName).WaitAsync(cts.Token);
        }

        // Single join tests ... multi grain, multi reminders

        /// <summary>
        /// Tests single join scenario with multiple grains and multiple reminders.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests handling of reminder not found scenarios.
        /// </summary>
        [Fact]
        public async Task Rem_Grain_ReminderNotFounds()
        {
            await Test_Reminders_ReminderNotFound(TestContext.Current.CancellationToken);
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
