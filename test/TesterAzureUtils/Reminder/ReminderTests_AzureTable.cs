using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using System.Linq;
using UnitTests.TimerTests;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace Tester.AzureUtils.TimerTests
{
    [TestCategory("ReminderService"), TestCategory("Azure")]
    public class ReminderTests_AzureTable : ReminderTests_Base, IClassFixture<ReminderTests_AzureTable.Fixture>
    {
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                Guid serviceId = Guid.NewGuid();
                var options = new TestClusterOptions();

                options.ClusterConfiguration.Globals.ServiceId = serviceId;
                options.ClusterConfiguration.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.AzureTable;

                return new TestCluster(options);
            }
        }

        public ReminderTests_AzureTable(Fixture fixture) : base(fixture)
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

        #region Basic test
        [SkippableFact, TestCategory("Functional")]
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

        [SkippableFact, TestCategory("Functional")]
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
        #endregion

        #region Basic single grain multi reminders test
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_MultipleReminders()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await PerGrainMultiReminderTest(grain);
        }
        #endregion

        #region Multiple joins ... multi grain, multi reminders
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_2J_MultiGrainMultiReminders()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task<bool>[] tasks =
            {
                Task.Run(() => PerGrainMultiReminderTestChurn(g1)),
                Task.Run(() => PerGrainMultiReminderTestChurn(g2)),
                Task.Run(() => PerGrainMultiReminderTestChurn(g3)),
                Task.Run(() => PerGrainMultiReminderTestChurn(g4)),
                Task.Run(() => PerGrainMultiReminderTestChurn(g5)),
            };

            await Task.Delay(period.Multiply(5));

            // start two extra silos ... although it will take it a while before they stabilize
            log.Info("Starting 2 extra silos");

            this.HostedCluster.StartAdditionalSilos(2);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            //Block until all tasks complete.
            await Task.WhenAll(tasks).WithTimeout(ENDWAIT);
        }
        #endregion

        #region Multi grains multi reminders/grain test
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_MultiGrainMultiReminders()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            Task<bool>[] tasks =
            {
                Task.Run(() => PerGrainMultiReminderTest(g1)),
                Task.Run(() => PerGrainMultiReminderTest(g2)),
                Task.Run(() => PerGrainMultiReminderTest(g3)),
                Task.Run(() => PerGrainMultiReminderTest(g4)),
                Task.Run(() => PerGrainMultiReminderTest(g5)),
            };

            //Block until all tasks complete.
            await Task.WhenAll(tasks).WithTimeout(ENDWAIT);
        }
        #endregion

        #region Secondary failure ... Basic test

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1F_Basic()
        {
            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task<bool> test = Task.Run(async () => { await PerGrainFailureTest(g1); return true; });

            Thread.Sleep(period.Multiply(failAfter));
            // stop the secondary silo
            log.Info("Stopping secondary silo");
            this.HostedCluster.StopSilo(this.HostedCluster.SecondarySilos.First());

            await test; // Block until test completes.
        }
        #endregion

        #region Multiple failures ... multiple grains
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_2F_MultiGrain()
        {
            List<SiloHandle> silos = this.HostedCluster.StartAdditionalSilos(2);

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task[] tasks =
            {
                Task.Run(() => PerGrainFailureTest(g1)),
                Task.Run(() => PerGrainFailureTest(g2)),
                Task.Run(() => PerGrainFailureTest(g3)),
                Task.Run(() => PerGrainFailureTest(g4)),
                Task.Run(() => PerGrainFailureTest(g5)),
            };

            Thread.Sleep(period.Multiply(failAfter));

            // stop a couple of silos
            log.Info("Stopping 2 silos");
            int i = random.Next(silos.Count);
            this.HostedCluster.StopSilo(silos[i]);
            silos.RemoveAt(i);
            this.HostedCluster.StopSilo(silos[random.Next(silos.Count)]);

            await Task.WhenAll(tasks).WithTimeout(ENDWAIT); // Block until all tasks complete.
        }
        #endregion

        #region 1 join 1 failure simulateneously ... multiple grains
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_1F1J_MultiGrain()
        {
            List<SiloHandle> silos = this.HostedCluster.StartAdditionalSilos(1);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g3 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g4 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g5 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task[] tasks =
            {
                Task.Run(() => PerGrainFailureTest(g1)),
                Task.Run(() => PerGrainFailureTest(g2)),
                Task.Run(() => PerGrainFailureTest(g3)),
                Task.Run(() => PerGrainFailureTest(g4)),
                Task.Run(() => PerGrainFailureTest(g5)),
            };

            Thread.Sleep(period.Multiply(failAfter));

            var siloToKill = silos[random.Next(silos.Count)];
            // stop a silo and join a new one in parallel
            log.Info("Stopping a silo and joining a silo");
            Task<bool> t1 = Task.Factory.StartNew(() =>
            {

                this.HostedCluster.StopSilo(siloToKill);
                return true;
            });
            Task<bool> t2 = Task.Factory.StartNew(() =>
            {
                this.HostedCluster.StartAdditionalSilos(1);
                return true;
            });
            await Task.WhenAll(new[] { t1, t2 }).WithTimeout(ENDWAIT);

            await Task.WhenAll(tasks).WithTimeout(ENDWAIT); // Block until all tasks complete.
            log.Info("\n\n\nReminderTest_1F1J_MultiGrain passed OK.\n\n\n");
        }
        #endregion

        #region Register same reminder multiple times
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_RegisterSameReminderTwice()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            Task<IGrainReminder> promise1 = grain.StartReminder(DR);
            Task<IGrainReminder> promise2 = grain.StartReminder(DR);
            Task<IGrainReminder>[] tasks = { promise1, promise2 };
            await Task.WhenAll(tasks).WithTimeout(TimeSpan.FromSeconds(15));
            //Assert.NotEqual(promise1.Result, promise2.Result);
            // TODO: write tests where period of a reminder is changed
        }
        #endregion

        #region Multiple grain types
        [SkippableFact, TestCategory("Functional")]
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

        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_GT_1F1J_MultiGrain()
        {
            List<SiloHandle> silos = this.HostedCluster.StartAdditionalSilos(1);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            IReminderTestGrain2 g1 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestGrain2 g2 = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            IReminderTestCopyGrain g3 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());
            IReminderTestCopyGrain g4 = this.GrainFactory.GetGrain<IReminderTestCopyGrain>(Guid.NewGuid());

            TimeSpan period = await g1.GetReminderPeriod(DR);

            Task[] tasks =
            {
                Task.Run(() => PerGrainFailureTest(g1)),
                Task.Run(() => PerGrainFailureTest(g2)),
                Task.Run(() => PerCopyGrainFailureTest(g3)),
                Task.Run(() => PerCopyGrainFailureTest(g4)),
            };

            Thread.Sleep(period.Multiply(failAfter));

            var siloToKill = silos[random.Next(silos.Count)];
            // stop a silo and join a new one in parallel
            log.Info("Stopping a silo and joining a silo");
            Task<bool> t1 = Task.Run(() =>
            {
                this.HostedCluster.StopSilo(siloToKill);
                return true;
            });
            Task<bool> t2 = Task.Run(() =>
            {
                this.HostedCluster.StartAdditionalSilos(1);
                return true;
            });
            await Task.WhenAll(new[] { t1, t2 }).WithTimeout(ENDWAIT);

            await Task.WhenAll(tasks).WithTimeout(ENDWAIT); // Block until all tasks complete.
        }
        #endregion       

        #region Testing things that should fail

        #region Lower than allowed reminder period
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Wrong_LowerThanAllowedPeriod()
        {
            IReminderTestGrain2 grain = this.GrainFactory.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
            await Assert.ThrowsAsync<ArgumentException>(() =>
                grain.StartReminder(DR, TimeSpan.FromMilliseconds(3000), true));
        }
        #endregion

        #region The wrong reminder grain
        [SkippableFact, TestCategory("Functional")]
        public async Task Rem_Azure_Wrong_Grain()
        {
            IReminderGrainWrong grain = this.GrainFactory.GetGrain<IReminderGrainWrong>(0);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                grain.StartReminder(DR));
        }
        #endregion

        #endregion
    }

}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
