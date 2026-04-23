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
    [TestCategory("Reminders"), TestCategory("AzureStorage")]
    public class ReminderTests_AzureTable : ReminderTestsBase, IClassFixture<ReminderTests_AzureTable.Fixture>
    {
        public class Fixture : BaseInProcessAzureTestClusterFixture
        {
            private ReminderTestClock? _reminderClock;
            internal ReminderTestClock ReminderClock => _reminderClock ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");

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

        public ReminderTests_AzureTable(Fixture fixture) : base(fixture.ReminderClock, fixture.HostedCluster)
        {
            fixture.EnsurePreconditionsMet();
        }

        // Basic tests

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

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/9337"), TestCategory("Functional")]
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

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/9344"), TestCategory("Functional")]
        public async Task Rem_Azure_Basic()
        {
            // start up a test grain and get the period that it's programmed to use.
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            TimeSpan period = await grain.GetReminderPeriod(DR);
            // start up the 'DR' reminder and wait for two ticks to pass.
            await grain.StartReminder(DR);
            Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
            // retrieve the value of the counter-- it should match the sequence number which is the number of periods
            // we've waited.
            long last = await grain.GetCounter(DR);
            Assert.Equal(2, last);
            // stop the timer and wait for a whole period.
            await grain.StopReminder(DR);
            Thread.Sleep(period.Multiply(1) + LEEWAY); // giving some leeway
            // the counter should not have changed.
            long curr = await grain.GetCounter(DR);
            Assert.Equal(last, curr);
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/9557"), TestCategory("Functional")]
        public async Task Rem_Azure_Basic_Restart()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            TimeSpan period = await grain.GetReminderPeriod(DR);
            await grain.StartReminder(DR);
            Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
            long last = await grain.GetCounter(DR);
            Assert.Equal(2, last);

            await grain.StopReminder(DR);
            TimeSpan sleepFor = period.Multiply(1) + LEEWAY;
            Thread.Sleep(sleepFor); // giving some leeway
            long curr = await grain.GetCounter(DR);
            Assert.Equal(last, curr);
            AssertIsInRange(curr, last, last + 1, grain, DR, sleepFor);

            // start the same reminder again
            await grain.StartReminder(DR);
            sleepFor = period.Multiply(2) + LEEWAY;
            Thread.Sleep(sleepFor); // giving some leeway
            curr = await grain.GetCounter(DR);
            AssertIsInRange(curr, 2, 3, grain, DR, sleepFor);
            await grain.StopReminder(DR); // cleanup
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

            Task<bool>[] tasks =
            {
                Task.Run(() => PerGrainMultiReminderTestChurn(g1, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTestChurn(g2, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTestChurn(g3, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTestChurn(g4, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTestChurn(g5, cts.Token), cts.Token),
            };

            await WaitForInitialReminderTicksAsync(cts.Token, g1, g2, g3, g4, g5);

            // start two extra silos ... although it will take it a while before they stabilize
            log.LogInformation("Starting 2 extra silos");

            await this.StartAdditionalSilosAsync(2, true).WaitAsync(cts.Token);
            await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);

            //Block until all tasks complete.
            await Task.WhenAll(tasks).WaitAsync(cts.Token);
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

            Task<bool>[] tasks =
            {
                Task.Run(() => PerGrainMultiReminderTest(g1, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTest(g2, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTest(g3, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTest(g4, cts.Token), cts.Token),
                Task.Run(() => PerGrainMultiReminderTest(g5, cts.Token), cts.Token),
            };

            //Block until all tasks complete.
            await Task.WhenAll(tasks).WaitAsync(cts.Token);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1F_Basic()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            Task<bool> test = Task.Run(() => PerGrainFailureTest(g1, cts.Token), cts.Token);

            await WaitForReminderCounterAsync(g1, DR, () => g1.GetCounter(DR), failAfter, cts.Token);
                // stop the secondary silo
                await using (await PauseReminderTimeAsync(cts.Token))
                {
                    log.LogInformation("Stopping secondary silo");
                    await this.StopSiloAsync(this.GetSecondarySilo());
                    await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
                }

            await test.WaitAsync(cts.Token); // Block until test completes.
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_2F_MultiGrain()
        {
            var silos = await this.StartAdditionalSilosAsync(2, true);

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            Task[] tasks =
            {
                Task.Run(() => PerGrainFailureTest(g1, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g2, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g3, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g4, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g5, cts.Token), cts.Token),
            };

            await WaitForInitialReminderTicksAsync(cts.Token, g1, g2, g3, g4, g5);
            await WaitForReminderCounterAsync(g1, DR, () => g1.GetCounter(DR), failAfter, cts.Token);

            // stop a couple of silos
            await using (await PauseReminderTimeAsync(cts.Token))
            {
                log.LogInformation("Stopping 2 silos");
                int i = Random.Shared.Next(silos.Count);
                    await this.StopSiloAsync(silos[i]);
                    silos.RemoveAt(i);
                    await this.StopSiloAsync(silos[Random.Shared.Next(silos.Count)]);
                    await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
                }

            await Task.WhenAll(tasks).WaitAsync(cts.Token); // Block until all tasks complete.
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1F1J_MultiGrain()
        {
            var silos = await this.StartAdditionalSilosAsync(1);
            await this.WaitForLivenessToStabilizeAsync();

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            Task[] tasks =
            {
                Task.Run(() => PerGrainFailureTest(g1, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g2, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g3, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g4, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g5, cts.Token), cts.Token),
            };

            await WaitForInitialReminderTicksAsync(cts.Token, g1, g2, g3, g4, g5);
            await WaitForReminderCounterAsync(g1, DR, () => g1.GetCounter(DR), failAfter, cts.Token);

            var siloToKill = silos[Random.Shared.Next(silos.Count)];
                // stop a silo and join a new one in parallel
                await using (await PauseReminderTimeAsync(cts.Token))
                {
                    log.LogInformation("Stopping a silo and joining a silo");
                    Task t1 = this.StopSiloAsync(siloToKill);
                    Task t2 = this.StartAdditionalSilosAsync(1, true);
                    await Task.WhenAll(new[] { t1, t2 }).WaitAsync(cts.Token);
                    await this.WaitForLivenessToStabilizeAsync().WaitAsync(cts.Token);
                }

            await Task.WhenAll(tasks).WaitAsync(cts.Token); // Block until all tasks complete.
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

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/9557"), TestCategory("Functional")]
        public async Task Rem_Azure_GT_Basic()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestCopyGrain g2 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            TimeSpan period = await g1.GetReminderPeriod(DR); // using same period

            await g1.StartReminder(DR);
            Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
            await g2.StartReminder(DR);
            Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
            long last1 = await g1.GetCounter(DR);
            Assert.Equal(4, last1);
            long last2 = await g2.GetCounter(DR);
            Assert.Equal(2, last2); // CopyGrain fault

            await g1.StopReminder(DR);
            Thread.Sleep(period.Multiply(2) + LEEWAY); // giving some leeway
            await g2.StopReminder(DR);
            long curr1 = await g1.GetCounter(DR);
            Assert.Equal(last1, curr1);
            long curr2 = await g2.GetCounter(DR);
            Assert.Equal(4, curr2); // CopyGrain fault
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/4319"), TestCategory("Functional")]
        public async Task Rem_Azure_GT_1F1J_MultiGrain()
        {
            var silos = await this.StartAdditionalSilosAsync(1);
            await this.WaitForLivenessToStabilizeAsync();

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestCopyGrain g3 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            IReminderTestCopyGrain g4 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            using var cts = new CancellationTokenSource(ENDWAIT);

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task[] tasks =
            {
                Task.Run(() => PerGrainFailureTest(g1, cts.Token), cts.Token),
                Task.Run(() => PerGrainFailureTest(g2, cts.Token), cts.Token),
                Task.Run(() => PerCopyGrainFailureTest(g3, cts.Token), cts.Token),
                Task.Run(() => PerCopyGrainFailureTest(g4, cts.Token), cts.Token),
            };

            Thread.Sleep(period.Multiply(failAfter));

            var siloToKill = silos[Random.Shared.Next(silos.Count)];
            // stop a silo and join a new one in parallel
            log.LogInformation("Stopping a silo and joining a silo");
            Task t1 = this.StopSiloAsync(siloToKill);
            Task t2 = this.StartAdditionalSilosAsync(1);
            await Task.WhenAll(new[] { t1, t2 }).WaitAsync(cts.Token);

            await Task.WhenAll(tasks).WaitAsync(cts.Token); // Block until all tasks complete.
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
