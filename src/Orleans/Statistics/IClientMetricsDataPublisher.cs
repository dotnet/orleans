using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    public interface IClientMetricsDataPublisher
    {
        Task Init(ClientConfiguration config, IPAddress address, string clientId);
        Task ReportMetrics(IClientPerformanceMetrics metricsData);
    }
}