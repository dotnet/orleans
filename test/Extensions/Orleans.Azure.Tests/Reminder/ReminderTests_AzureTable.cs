#nullable enable

using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using Microsoft.Extensions.Logging;
using Orleans.Testing.Reminders;
using UnitTests.TimerTests;
using Orleans.Internal;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace Tester.AzureUtils.TimerTests
{
    /// <summary>
    /// Tests for Azure Table Storage-based reminder service, including basic operations, failover, and multi-grain scenarios.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("AzureStorage")]
    [TestArea("Reminders")]
    [TestCategory("Reminders"), TestCategory("AzureStorage")]
    public class ReminderTests_AzureTable : ReminderTestsBase, IClassFixture<ReminderTests_AzureTable.Fixture>
    {
        private readonly Fixture _fixture;

        public class Fixture : BaseInProcessAzureTestClusterFixture
        {
            private static readonly TimeSpan ReminderServiceStartupTimeout = TimeSpan.FromMinutes(5);
            private ReminderTestClock? _reminderClock;
            private readonly ReminderDiagnosticObserver _startupObserver = ReminderDiagnosticObserver.Create();
            private IReadOnlyList<SiloAddress>? _initialSilos;
            private IReadOnlyList<SiloAddress>? _startedReminderServices;
            internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");
            internal IReadOnlyList<SiloAddress> InitialSilos => _initialSilos
                ?? throw new InvalidOperationException("The initial silos have not been captured.");
            internal IReadOnlyList<SiloAddress> StartedReminderServices => _startedReminderServices
                ?? throw new InvalidOperationException("Reminder services have not completed startup.");

            protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
            {
                _reminderClock = builder.AddReminderTestClock();
                builder.ConfigureSilo((_, siloBuilder) =>
                {
                    siloBuilder.UseAzureTableReminderService(options =>
                    {
                        options.ConfigureTestDefaults();
                    });
                });
            }

            public override async Task InitializeAsync()
            {
                await base.InitializeAsync();

                var silos = HostedCluster.Silos.ToArray();
                _initialSilos = silos.Select(silo => silo.SiloAddress).ToArray();
                using var cancellation = new CancellationTokenSource(ReminderServiceStartupTimeout);
                var startedTasks = silos
                    .Select(silo => _startupObserver.WaitForReminderServiceStartedAsync(cancellation.Token, silo.SiloAddress))
                    .ToArray();

                try
                {
                    _startedReminderServices = (await Task.WhenAll(startedTasks))
                        .Select(started => started.SiloAddress!)
                        .ToArray();
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    var missing = silos
                        .Where((_, index) => !startedTasks[index].IsCompletedSuccessfully)
                        .Select(silo => silo.SiloAddress);
                    throw new TimeoutException(
                        $"Azure reminder services did not start within {ReminderServiceStartupTimeout}. Missing silos: {string.Join(", ", missing)}.");
                }
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

        public ReminderTests_AzureTable(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
        {
            _fixture = fixture;
            fixture.EnsurePreconditionsMet();
        }

        // Basic tests

        [SkippableFact, TestCategory("Functional")]
        public void Fixture_WaitsForReminderServicesToStart()
        {
            Assert.Equal(_fixture.InitialSilos, _fixture.StartedReminderServices);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Basic_StopByRef()
        {
            await Test_Reminders_Basic_StopByRef();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_UpdateReminder_DoesNotRestartLocalReminder()
        {
            await Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Basic_ListOps()
        {
            await Test_Reminders_Basic_ListOps();
        }

        // Single join tests ... multi grain, multi reminders

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1J_MultiGrainMultiReminders()
        {
            await Test_Reminders_1J_MultiGrainMultiReminders();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_ReminderNotFound()
        {
            await Test_Reminders_ReminderNotFound();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Basic()
        {
            using var cts = new CancellationTokenSource(ENDWAIT);
            var grain = GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            var period = await grain.GetReminderPeriod(DR);

            await grain.StartReminder(DR);
            await AdvanceRemindersByTicksAsync(2, cts.Token, GetReminderIdentities([grain], DR));
            var last = await grain.GetCounter(DR);
            Assert.Equal(2, last);

            await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cts.Token);
            await AdvanceReminderTimeAsync(period, cts.Token);
            var curr = await grain.GetCounter(DR);
            Assert.Equal(last, curr);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Basic_Restart()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            TimeSpan period = await grain.GetReminderPeriod(DR);
            using var cts = new CancellationTokenSource(ENDWAIT);

            await grain.StartReminder(DR);
            await AdvanceRemindersByTicksAsync(2, cts.Token, (grain, DR));
            await AssertReminderCountersAsync([grain], (DR, 2));

            await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cts.Token);
            await AdvanceReminderTimeAsync(period, cts.Token);
            await AssertReminderCountersAsync([grain], (DR, 2));

            // start the same reminder again
            await grain.StartReminder(DR);
            await AdvanceRemindersByTicksAsync(2, cts.Token, (grain, DR));
            await AssertReminderCountersAsync([grain], (DR, 2));
            await StopReminderAndWaitForQuiescenceAsync(grain, DR, grain.StopReminder, cts.Token);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_MultipleReminders()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_2J_MultiGrainMultiReminders()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(CHURN_ENDWAIT);

            await Test_Reminders_MultiGrainMultiReminders(
                async cancellationToken =>
                {
                    await using (await PauseReminderTimeAsync(cancellationToken))
                    {
                        log.LogInformation("Starting 2 extra silos");
                        await this.StartAdditionalSilosAndWaitForReminderServicesAsync(
                            2,
                            cancellationToken,
                            startAdditionalSiloOnNewPort: true);
                    }
                },
                cts.Token,
                g1,
                g2,
                g3,
                g4,
                g5);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_MultiGrainMultiReminders()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            await Test_Reminders_MultiGrainMultiReminders(
                afterFirstTick: null,
                cts.Token,
                g1,
                g2,
                g3,
                g4,
                g5);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1F_Basic()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            await PrepareForGrainFailureAsync(cts.Token, g1);

            // stop the secondary silo
            await using (await PauseReminderTimeAsync(cts.Token))
            {
                var reminderOwner = this.GetReminderOwner(g1, DR);
                log.LogInformation("Stopping reminder owner {SiloAddress}", reminderOwner.SiloAddress);
                await this.StopSiloAsync(reminderOwner);
                await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
            }

            await CompleteGrainFailureTestAsync(cts.Token, g1);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_2F_MultiGrain()
        {
            using var cts = new CancellationTokenSource(ENDWAIT);
            _ = await this.StartAdditionalSilosAndWaitForReminderServicesAsync(
                2,
                cts.Token,
                startAdditionalSiloOnNewPort: true);

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            IAddressable[] grains = [g1, g2, g3, g4, g5];
            await PrepareForGrainFailureAsync(cts.Token, grains);

            // stop a couple of silos
            await using (await PauseReminderTimeAsync(cts.Token))
            {
                log.LogInformation("Stopping 2 silos");
                var reminderOwner = this.GetReminderOwner(g1, DR);
                var otherSilo = this.HostedCluster.GetActiveSilos().First(silo => !silo.SiloAddress.Equals(reminderOwner.SiloAddress));
                await this.StopSiloAsync(reminderOwner);
                await this.StopSiloAsync(otherSilo);
                await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
            }

            await CompleteGrainFailureTestAsync(cts.Token, grains);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1F1J_MultiGrain()
        {
            using var cts = new CancellationTokenSource(ENDWAIT);
            _ = await this.StartAdditionalSilosAndWaitForReminderServicesAsync(1, cts.Token);

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            IAddressable[] grains = [g1, g2, g3, g4, g5];
            await PrepareForGrainFailureAsync(cts.Token, grains);

            var siloToKill = this.GetReminderOwner(g1, DR);
            // stop a silo and join a new one in parallel
            await using (await PauseReminderTimeAsync(cts.Token))
            {
                log.LogInformation("Stopping a silo and joining a silo");
                await this.StopSiloAndStartAdditionalSiloAsync(
                    siloToKill,
                    cts.Token,
                    startAdditionalSiloOnNewPort: true);
            }

            await CompleteGrainFailureTestAsync(cts.Token, grains);
            log.LogInformation("\n\n\nReminderTest_1F1J_MultiGrain passed OK.\n\n\n");
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_RegisterSameReminderTwice()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            Task<IGrainReminder> promise1 = grain.StartReminder(DR);
            Task<IGrainReminder> promise2 = grain.StartReminder(DR);
            Task<IGrainReminder>[] tasks = { promise1, promise2 };
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
            //Assert.NotEqual(promise1.Result, promise2.Result);
            // TODO: write tests where period of a reminder is changed
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_GT_Basic()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestCopyGrain g2 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            await g1.StartReminder(DR);
            await AdvanceRemindersByTicksAsync(2, cts.Token, (g1, DR));
            await AssertReminderCountersAsync([g1], (DR, 2));

            await g2.StartReminder(DR);
            await AdvanceRemindersByTicksAsync(2, cts.Token, (g1, DR), (g2, DR));
            await AssertReminderCountersAsync([g1], (DR, 4));
            await AssertReminderCountersAsync([g2], (DR, 2));

            await StopReminderAndWaitForQuiescenceAsync(g1, DR, g1.StopReminder, cts.Token);
            await AdvanceRemindersByTicksAsync(2, cts.Token, (g2, DR));
            await AssertReminderCountersAsync([g1, g2], (DR, 4));
            await StopReminderAndWaitForQuiescenceAsync(g2, DR, g2.StopReminder, cts.Token);
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4319"), TestCategory("Functional")]
        public async Task Rem_Azure_GT_1F1J_MultiGrain()
        {
            using var cts = new CancellationTokenSource(ENDWAIT);
            _ = await this.StartAdditionalSilosAndWaitForReminderServicesAsync(1, cts.Token);

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestCopyGrain g3 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            IReminderTestCopyGrain g4 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());

            IAddressable[] grains = [g1, g2, g3, g4];
            await PrepareForGrainFailureAsync(cts.Token, grains);

            var siloToKill = this.GetReminderOwner(g1, DR);
            // stop a silo and join a new one in parallel
            await using (await PauseReminderTimeAsync(cts.Token))
            {
                log.LogInformation("Stopping a silo and joining a silo");
                await this.StopSiloAndStartAdditionalSiloAsync(siloToKill, cts.Token);
            }

            await CompleteGrainFailureTestAsync(cts.Token, grains);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Wrong_LowerThanAllowedPeriod()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await Assert.ThrowsAsync<ArgumentException>(() =>
                grain.StartReminder(DR, TimeSpan.FromMilliseconds(3000), true));
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Wrong_Grain()
        {
            IReminderGrainWrong grain = this.GrainFactory.GetGrain<IReminderGrainWrong>(0);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                grain.StartReminder(DR));
        }
    }

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
