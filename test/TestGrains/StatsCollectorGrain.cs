using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;

namespace UnitTests.Stats
{
    [PreferLocalPlacement]
    public class StatsCollectorGrain : Grain, IStatsCollectorGrain
    {
        private long numStatsCalls;
        private long numMetricsCalls;

        public Task ReportMetricsCalled()
        {
            numStatsCalls++;
            return TaskDone.Done;
        }

        public Task ReportStatsCalled()
        {
            numMetricsCalls++;
            return TaskDone.Done;
        }

        public Task<long> GetReportMetricsCallCount()
        {
            return Task.FromResult(numMetricsCalls);
        }

        public Task<long> GetReportStatsCallCount()
        {
            return Task.FromResult(numStatsCalls);
        }
    }
}