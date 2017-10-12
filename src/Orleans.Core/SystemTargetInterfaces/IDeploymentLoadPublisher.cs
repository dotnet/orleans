using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface IDeploymentLoadPublisher : ISystemTarget
    {
        Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats);
    }
}
