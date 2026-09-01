using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    internal interface IDeploymentLoadPublisher : ISystemTarget
    {
        [OneWay]
        [Alias("C5255F0C")]
        Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats, CancellationToken cancellationToken = default);
    }
}
