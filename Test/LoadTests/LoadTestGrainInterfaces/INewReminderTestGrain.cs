using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace LoadTestGrainInterfaces
{

    public interface INewReminderTestGrain : IGrain
    {
        Task EnableLogging();
        Task DisableLogging();

        //Task ClearSiloReminderTable();

        Task Reset();
        Task StartReminder(string name, TimeSpan delay, TimeSpan period);
        Task StopReminder(string name);
        Task ResetReminder(string name);
        Task<NewReminderTestStats> GetTestStats(string name);
    }

    [Serializable]
    public class NewReminderTestStats
    {
        public IGrainReminder Handle { get; private set; }
        public string Name { get; private set; }
        public DateTime FirstTimestamp { get; private set; }
        public DateTime LatestTimestamp { get; private set; }
        public long SampleSize { get; private set; }
        public TimeSpan ExpectedPeriod { get; private set; }
        public TimeSpan Delay { get; private set; }
        public List<TimeSpan> ActualPeriods { get; private set; }
        public TimeSpan AveragePeriod
        {
            get
            {
                return
                    TimeSpan.FromMilliseconds(
                        ActualPeriods.Select(
                            ts =>
                                ts.TotalMilliseconds)
                        .Average());
            }
        }

        private NewReminderTestStats()
        { }

        public static NewReminderTestStats
            NewObject(
                IGrainReminder handle,
                TimeSpan delay,
                TimeSpan period)
        {
            if (handle == null)
                throw new ArgumentNullException("handle");

            return
                new NewReminderTestStats
                    {
                        Handle = handle,
                        Name = handle.ReminderName,
                        FirstTimestamp = default(DateTime),
                        LatestTimestamp = default(DateTime),
                        SampleSize = 0,
                        Delay = delay,
                        ExpectedPeriod = period,
                        ActualPeriods = new List<TimeSpan>()
                    };
        }

        public void Update(TickStatus status)
        {
            if (FirstTimestamp == default(DateTime))
            {
                FirstTimestamp = status.FirstTickTime;
                LatestTimestamp = status.CurrentTickTime;
                SampleSize = 1;
            }
            else if (FirstTimestamp != status.FirstTickTime)
                throw new InvalidOperationException("Unexpected mismatch of reminder tracking data.");
            else
            {
                var actualPeriod = status.CurrentTickTime - LatestTimestamp;
                ActualPeriods.Add(actualPeriod);
                LatestTimestamp = status.CurrentTickTime;
                ++SampleSize;
            }
        }

        public void Retire()
        {
            Handle = null;
        }

        public bool IsRetired { get { return Handle == null; } }

        public TimeSpan Lifespan { get { return LatestTimestamp - FirstTimestamp; } }

        public IEnumerable<double> DeviationsFromExpectedPeriod()
        {
            var expected = ExpectedPeriod.TotalSeconds;
            return 
                ActualPeriods.Select(
                    ts =>
                        ts.TotalSeconds - expected);
        }

        //public static string PrintHistogram(IEnumerable<NewReminderTestStats> statsCollection)
        //{
        //    var deviationsInTicks =
        //        statsCollection.SelectMany(
        //            stats =>
        //                stats.DeviationsFromExpectedPeriod())
        //        .Select(
        //            secs =>
        //                TimeSpan.FromSeconds(Math.Abs(secs)).Ticks);
        //    // [mlr] the number of buckets should be large; each bucket 
        //    // corresponds to 2^n ticks.
        //    return 
        //        HistogramValueStatistic.PrintHistogram(
        //            deviationsInTicks, 
        //            numBuckets: 100, 
        //            inMsec: true);
        //}

        public double AverageDeviationFromExpectedPeriod()
        {
            return DeviationsFromExpectedPeriod().Average();
        }

        public override string ToString()
        {
            return
                string.Format(
                    "[ReminderTestStats: name={0}, retired?={1}, expected={2}, deviation={3}, sz={4}, lifespan={5}]",
                    Name,
                    IsRetired,
                    ExpectedPeriod,
                    AverageDeviationFromExpectedPeriod(),
                    SampleSize,
                    Lifespan);
        }
    }
}
