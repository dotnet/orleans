using System;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using NewReminderLoadTest;
using Orleans;
using StreamPullingAgentBenchmark.EmbeddedSiloLoadTest;

namespace StreamPullingAgentBenchmark.NewReminderLoadTest
{
    public class NewReminderLoadTest : BaseEmbeddedSiloLoadTest<NewReminderOptions>
    {
        private ReminderRegisterer _registerer;

        private ISharedMemoryCounterAggregatorGrain _reminderTickCountGrain;

        private long _totalReminderTicks;

        protected override Task InitializeAsync()
        {
            _registerer = new ReminderRegisterer(_options);
            _registerer.StartRegistering();

            _reminderTickCountGrain = SharedMemoryCounterAggregatorGrainFactory.GetGrain(0);

            return TaskDone.Done;
        }

        protected override Task CleanupAsync()
        {
            _registerer.StopRegistering();

            return TaskDone.Done;
        }

        protected override Task StartPhaseAsync(string phaseName)
        {
            _totalReminderTicks = 0;

            return TaskDone.Done;
        }

        protected override async Task PollPeriodAsync(string phaseName, int iterationCount, TimeSpan duration)
        {
            var values = _registerer.Flush();

            long reminderTicks = await _reminderTickCountGrain.Poll();
            _totalReminderTicks += reminderTicks;

            string report = GenerateReport(duration, values, reminderTicks);

            // the following line is parsed by the framework.
            Utilities.LogAlways(string.Format("=*=== Period #{0} ran for {1} sec: {2}",
                iterationCount,
                duration.TotalSeconds,
                report));
        }

        protected override Task EndPhaseAsync(string phaseName, TimeSpan duration)
        {
            var finalValues = _registerer.Total();
            string finalReport = GenerateReport(duration, finalValues, _totalReminderTicks);
            Utilities.LogAlways(string.Format("=***= {0} {1}", phaseName, finalReport));

            return TaskDone.Done;
        }

        private static string GenerateReport(TimeSpan duration, ReminderRegisterer.ReportedValues values, long reminderTicks)
        {
            double reminderRegistrationsPerSec = CalculateTps(values.ReminderSuccessCount, duration);
            double successRatio = (double)values.ReminderSuccessCount / values.ReminderAttemptCount;
            double reminderTicksPerSec = CalculateTps(reminderTicks, duration);

            return string.Format("Reminder Registrations per sec: {0}, Success ratio: {1}, Reminder Registration latency: {2}, Reminder Ticks per sec: {3}",
                reminderRegistrationsPerSec,
                successRatio,
                values.AverageReminderLatency,
                reminderTicksPerSec);
        }

        private static double CalculateTps(long count, TimeSpan period)
        {
            if (TimeSpan.Zero == period)
            {
                throw new ArgumentException("Argument is zero", "period");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count", count, "count is less than zero");
            }

            return count / period.TotalSeconds;
        }
    }
}