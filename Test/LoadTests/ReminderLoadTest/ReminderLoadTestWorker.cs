using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LoadTestBase;
using LoadTestGrainInterfaces;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;

namespace ReminderLoadTest
{
    public class ReminderLoadTestWorker : OrleansClientWorkerBase
    {
        private readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(4);
        private readonly TimeSpan DefaultTestLength = TimeSpan.FromSeconds(60);
        private readonly Random RNG = new Random();
        private int? requestBase;
        private const int SystemManagementId = 1;

        public void ApplicationInitialize()
        {
        }

        protected override Task IssueRequest(int requestNumber, int threadNumber)
        {
            // [mlr] we run one test per request and serialize the requests. 
            // with each test, we want to increase the number of grains used.
            // the requests appear to start at 5000 and count upward, so we'll 
            // store the baseline so that we can calculate the number of grains
            // that we want to use.
            if (!requestBase.HasValue)
                requestBase = requestNumber;
            var testNumber = requestNumber - requestBase.Value + 1;
            const int step = 500;
            var grainsPerSilo = step * testNumber;

            WriteProgress(
                "NewReminderTests.IssueRequest(): starting test #{0} (request #{1})",
                testNumber,
                requestNumber);

            DeployReminders(grainsPerSilo, DefaultPeriod, DefaultTestLength).Wait();
            WriteProgress(
                "NewReminderTests.IssueRequest(): finished test #{0} (request #{1})",
                testNumber,
                requestNumber);

            return TaskDone.Done;
        }

        private async Task
            StartBasicTest(
                INewReminderTestGrain grain,
                TimeSpan reminderPeriod,
                string reminderName)
        {
            // [mlr][discuss] i think it would be useful to provide a default value for
            // delay in the API that performs this calculation for you.
            var delay = TimeSpan.FromSeconds(RNG.NextDouble() * reminderPeriod.TotalSeconds);
            await grain.Reset();
            // [mlr] we want to start the delay at the same time we start the reminder.
            // this way, we should be guaranteed that the sequence number we gather
            // later is never smaller than the expected number of sequences.
            await grain.StartReminder(reminderName, delay, reminderPeriod);
        }

        private Task<NewReminderTestStats>
            FinishBasicTest(
                INewReminderTestGrain grain,
                TimeSpan reminderPeriod,
                string reminderName)
        {
            var epsilon = reminderPeriod.TotalSeconds * 0.01;
            return FinishBasicTest(grain, reminderPeriod, reminderName, epsilon);
        }

        private async Task<NewReminderTestStats>
            FinishBasicTest(
                INewReminderTestGrain grain,
                TimeSpan reminderPeriod,
                string reminderName,
                double epsilon)
        {
            await grain.StopReminder(reminderName);
            var statsTask = grain.GetTestStats(reminderName);

            // [mlr] as grainCount -> infinity, the following statement becomes
            // impractical. feel free to uncomment it for debugging purposes but
            // you might want to reduce the number of grains created per silo.
            //await statsTask;
            //var stats = statsTask.Result;
            //logger.Info("NewReminderGrainTests.FinishBasicTest(): stats={0}", stats);
            //Assert.IsTrue(
            //    AreDoublesEqual(stats.AveragePeriod.TotalSeconds, reminderPeriod.TotalSeconds, epsilon),
            //    "A test reminder's average period deviated too far from the specified amount.");
            return await statsTask;
        }

        private async Task
            PerformBasicTest(
                int grainCount,
                TimeSpan reminderPeriod,
                TimeSpan testLength)
        {
            var grains = GenerateGrainRefs(grainCount);
            WriteProgress("NewReminderTests.PerformBasicTest(): reset");
            var resetTasks = grains.Select(g => g.Reset()).ToArray();
            await Task.WhenAll(resetTasks);
            var startTasks =
                grains.Select(
                    g =>
                        StartBasicTest(g, reminderPeriod, "default"))
                    .ToArray();
            await Task.WhenAll(startTasks);
            WriteProgress(
                "NewReminderTests.PerformBasicTest(): wait {0} s",
                testLength.TotalSeconds);
            await Task.Delay(testLength);
            WriteProgress("NewReminderTests.PerformBasicTest(): finishing");
            var finishTasks =
                grains.Select(
                    g =>
                        FinishBasicTest(g, reminderPeriod, "default"))
                    .ToArray();
            await Task.WhenAll(finishTasks);
            var allStats =
                finishTasks.Select(
                    t =>
                        t.Result);
            var avgdev =
                allStats.SelectMany(
                    s =>
                        s.DeviationsFromExpectedPeriod()).Average();
            WriteProgress(
                "NewReminderTests.PerformBasicTest(): finished avgdev = {0}", //, spread(msec)=\"{1}\"",
                avgdev);
                //NewReminderTestStats.PrintHistogram(allStats));
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

        public async Task DeployReminders(int grainsPerSilo, TimeSpan reminderPeriod, TimeSpan testLength)
        {
            var siloCount = await CollectActiveSiloCount();
            var grainCount = grainsPerSilo * siloCount;
            WriteProgress(
                "NewReminderTests.RemindersShouldScale(): starting {0} grains on {1} silos ({2} per silo).",
                grainCount,
                siloCount,
                grainsPerSilo);
            await PerformBasicTest(grainCount, reminderPeriod, testLength);
        }

        private async Task<int> CollectActiveSiloCount()
        {
            var mg = ManagementGrainFactory.GetGrain(SystemManagementId);
            var silos = await mg.GetHosts(onlyActive: true);
            return silos.Count;
        }

        public async Task RemindersShouldScale()
        {
            const int startAt = 1000;
            const int stopAt = 20000;
            const int step = 250;

            //int siloCount = GetActiveSilos().Count();
            int siloCount = 10;
            for (var i = startAt; i < stopAt + 1; i += step)
            {
                var grainCount = i * siloCount;
                WriteProgress(
                    "NewReminderTests.RemindersShouldScale(): starting {0} grains on {1} silos ({2} per silo).",
                    grainCount,
                    siloCount,
                    i);
                await PerformBasicTest(grainCount, DefaultPeriod, TimeSpan.FromMinutes(1));
            }
        }
    }
}