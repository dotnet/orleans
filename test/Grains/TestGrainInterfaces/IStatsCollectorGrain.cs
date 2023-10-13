namespace UnitTests.Stats
{
    public interface IStatsCollectorGrain : IGrainWithIntegerKey
    {
        Task ReportStatsCalled();
        
        Task<long> GetReportStatsCallCount();
    }
}