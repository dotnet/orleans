using System.Threading.Tasks;
using Orleans;

namespace UnitTests.Stats
{
    public interface IStatsCollectorGrain : IGrainWithIntegerKey
    {
        Task ReportMetricsCalled();
        Task ReportStatsCalled();

        Task<long> GetReportMetricsCallCount();
        Task<long> GetReportStatsCallCount();
    }
}