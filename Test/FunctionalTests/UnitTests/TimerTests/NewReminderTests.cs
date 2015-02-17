using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTestGrainInterfaces;
using System.IO;
using System.Linq;

namespace UnitTests.TimerTests
{
    using System.Diagnostics;

    [TestClass]
    public class NewReminderTests : UnitTestBase
    {
        private const int GrainCount = 10000;
        private readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(1);
        private const int DefaultPeriodMultiplier = 1;
        // for a small-scale test such as this, the periods ought to be really precisely triggered.
        private const double Epsilon = 0.01;

        public NewReminderTests() :
            base(new Options {SiloConfigFile = new FileInfo("Config_AzureTableStorage.xml")})
        {
        }

        [TestCleanup]
        public void CleanupTest()
        {
            // ReminderTable.Clear() cannot be called from a non-Orleans thread,
            // so we must proxy the call through a grain.
            var controlProxy = NewReminderTestGrainFactory.GetGrain(-1);
            controlProxy.ClearSiloReminderTable().Wait();

            // in this context, "reset" means to shut down a silo independent of whether
            // the silo is in-process (AppDomain, which can be stopped) or out-of-process (which
            // must be killed).
            ResetAllAdditionalRuntimes();
            ResetDefaultRuntimes();
        }

        private async Task
            PrepareTest(
                INewReminderTestGrain grain,
                TimeSpan reminderPeriod,
                string reminderName,
                DateTime startTestAt)
        {
            await grain.Reset();
            // discuss: i think it would be useful to provide a default value for
            // delay in the API that performs this calculation for you.
            var delay = (startTestAt - DateTime.UtcNow)
                + TimeSpan.FromSeconds(random.NextDouble() * reminderPeriod.TotalSeconds);
            // we want to start the delay at the same time we start the reminder.
            // this way, we should be guaranteed that the sequence number we gather
            // later is never smaller than the expected number of sequences.
            await grain.StartReminder(reminderName, delay, reminderPeriod);
        }

        private async Task
            PrepareTest(
                INewReminderTestGrain[] grains,
                TimeSpan reminderPeriod,
                string reminderName,
                DateTime startTestAt)
        {
            this.logger.Info("NewReminderTests.PrepareTest: preparing");
            // we want to use Task.ContinueWith with an async lambda, an explicitly typed variable is required to avoid
            // writing code that doesn't do what i think it is doing.
            Func<INewReminderTestGrain, Task> checkReminderCount =
                async grain =>
                {
                    var n = await grain.GetActiveReminderCount();
                    Assert.IsTrue(n > 0, "grain {0} has not started its reminder(s)", grain);
                };

            var prepTime = Stopwatch.StartNew();
            Task[] tasks =
                grains.Select(
                    g =>
                        this.PrepareTest(g, reminderPeriod, reminderName, startTestAt)
                            .ContinueWith(task => checkReminderCount(g)).Unwrap())
                .ToArray();
            await Task.WhenAll(tasks);

            prepTime.Stop();
            var preparedAt = DateTime.UtcNow;

            Assert.IsTrue(
                startTestAt > preparedAt,
                "reminder test did not have enough time to prepare itself (needed {0} sec).",
                prepTime.Elapsed.Seconds);
            this.logger.Info(
                "NewReminderTests.PrepareTest: prepared ({0} sec); test will start in {1} sec", 
                prepTime.Elapsed.Seconds,
                (startTestAt - preparedAt).TotalSeconds);
        }

        private async Task<NewReminderTestStats>
            FinishTest(
                INewReminderTestGrain grain,
                string reminderName)
        {
            await grain.StopReminder(reminderName);
            var stats1 = await grain.GetTestStats(reminderName);
            //logger.Info("NewReminderGrainTests.FinishTest(): wait stats={0}", stats1);

            // now that the reminder is stopped, wait for a whole period.
            await Task.Delay(stats1.RequestedPeriod);
            var stats2 = await grain.GetTestStats(reminderName);
            // the grain should not have gotten any additional ticks from our reminder.
            Assert.AreEqual(stats1.SampleSize, stats2.SampleSize, "A test reminder failed to stop.");
            return stats2;
        }

        private async Task<NewReminderTestStats[]>
            FinishTest(
                INewReminderTestGrain[] grains,
                string reminderName)
        {
            this.logger.Info("NewReminderTests.FinishTest: finishing");
            var tasks = grains.Select(g => this.FinishTest(g, reminderName)).ToArray();
            NewReminderTestStats[] results = await Task.WhenAll(tasks);
            return results;
        }

        private async Task<INewReminderTestGrain[]> ResetGrains(int grainCount)
        {
            var grains = GenerateGrainRefs(grainCount).ToArray();
            logger.Info("NewReminderTests.PerformBasicTest(): resetting");
            var tasks = grains.Select(g => g.Reset()).ToArray();
            await Task.WhenAll(tasks);
            return grains;
        }

        private async Task 
            PerformTest(
                INewReminderTestGrain[] grains, 
                TimeSpan reminderPeriod,
                int periodMultiplier)
        {
            var testLength = TimeSpan.FromSeconds(reminderPeriod.TotalSeconds * periodMultiplier);

            const string reminderName = "default";
            // the test length should last at least as long as two reminder periods.
            var requestedTestLength = testLength;
            testLength = TimeSpan.FromSeconds(Math.Max(requestedTestLength.TotalSeconds, reminderPeriod.TotalSeconds * 2));
            // based on observation, it takes approximately 30 sec. to create 100000 timers on the build machines,
            // which is expected to be slower than other hosts.
            var startTestAt = DateTime.UtcNow + TimeSpan.FromSeconds((((double)grains.Length / 10000) * 40) + 5);

            await PrepareTest(grains, reminderPeriod, reminderName, startTestAt);

            var delay = startTestAt - DateTime.UtcNow + testLength;
            this.logger.Info("NewReminderTests.PerformBasicTest: waiting {0} sec", delay.TotalSeconds);
            await Task.Delay(delay);

            var finishedAt = DateTime.UtcNow;
            var results = await FinishTest(grains, reminderName);
            
            var startedAt = results.Select(i => i.FirstTimestamp).Min();
            var actualTestLength = finishedAt - startTestAt;
            var ticksDelivered = results.Select(i => i.SampleSize).Sum();
            var actualTicksPerSecond = ticksDelivered / actualTestLength.TotalSeconds;
            var expectedTicksPerSecond = grains.Length / reminderPeriod.TotalSeconds;
            var percentDelivered = actualTicksPerSecond / expectedTicksPerSecond;
            logger.Info(
                "NewReminderTests.PerformBasicTest: results startedAt={0}, actualTestLength={1}, expectedTicksPerSecond={2} actualTicksPerSecond={3} ({4})",
                startedAt,
                actualTestLength,
                expectedTicksPerSecond,
                actualTicksPerSecond,
                percentDelivered);
            Assert.IsTrue(percentDelivered >= 0.97, "more than 3% of the requested ticks were not delivered.");
        }

        private Task
            PerformTest(
                INewReminderTestGrain[] grains,
                TimeSpan reminderPeriod)
        {
            return this.PerformTest(grains, reminderPeriod, DefaultPeriodMultiplier);
        }

        private IEnumerable<INewReminderTestGrain> GenerateGrainRefs(long count)
        {
            for (var i = 0; i < count; ++i)
                yield return NewReminderTestGrainFactory.GetGrain(i);
        }

        private static string GenerateReminderName(long n)
        {
            return n.ToString("X4");
        }

        private Task WarmUp()
        {
            var delay = TimeSpan.FromSeconds(30);
            this.logger.Info("NewReminderTests.WarmUp waiting {0} sec", delay.TotalSeconds);
            return Task.Delay(delay);
        }

        [TestMethod, TestCategory("NewReminderTests")]
        public async Task ReminderShouldTickGrainOnlyWhileRunning()
        {
            await this.WarmUp();
            this.logger.Info("NewReminderTests.ReminderShouldTickGrainOnlyWhileRunning");
            var grains = await this.ResetGrains(GrainCount);
            await this.PerformTest(grains, DefaultPeriod);
        }

        [TestMethod, TestCategory("NewReminderTests")]
        public async Task RemindersShouldBeAbleToBeRestarted()
        {
            await this.WarmUp();
            const int cycles = 2;
            this.logger.Info("NewReminderTests.RemindersShouldBeAbleToBeRestarted: cycles={0}", cycles);
            var grains = await this.ResetGrains(GrainCount);
            for (var i = 0; i < cycles; ++i)
                await this.PerformTest(grains, DefaultPeriod);
        }
    }
}
