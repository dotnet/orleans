using Orleans.Placement;

namespace UnitTests.Stats
{
    [PreferLocalPlacement]
    public class StatsCollectorGrain : Grain, IStatsCollectorGrain
    {
        private long numStatsCalls;

        public Task ReportStatsCalled()
        {
            numStatsCalls++;
            return Task.CompletedTask;
        }
        
        public Task<long> GetReportStatsCallCount()
        {
            return Task.FromResult(numStatsCalls);
        }
    }
}