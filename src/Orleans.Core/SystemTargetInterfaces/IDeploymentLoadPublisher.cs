using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    internal interface IDeploymentLoadPublisher : ISystemTarget
    {
        [OneWay]
        Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats);
    }
}
