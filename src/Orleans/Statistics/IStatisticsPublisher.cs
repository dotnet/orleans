using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public interface IStatisticsPublisher
    {
        Task ReportStats(List<ICounter> statsCounters);
        Task Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName);
    }
}