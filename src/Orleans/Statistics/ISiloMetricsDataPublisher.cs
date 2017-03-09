using System.Net;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public interface ISiloMetricsDataPublisher
    {
        Task Init(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName);
        Task ReportMetrics(ISiloPerformanceMetrics metricsData);
    }
}