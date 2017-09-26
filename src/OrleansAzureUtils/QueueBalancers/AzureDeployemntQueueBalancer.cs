using Orleans.Runtime;
using Orleans.Runtime.Host;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Streams.Azure
{
    public class DynamicAzureDeploymentBalancer : DeploymentBasedQueueBalancer
    {
        public DynamicAzureDeploymentBalancer(
            ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory)
            : base(siloStatusOracle, new ServiceRuntimeWrapper(loggerFactory), false)
        { }
    }

    public class StaticAzureDeploymentBalancer : DeploymentBasedQueueBalancer
    {
        public StaticAzureDeploymentBalancer(
            ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory)
            : base(siloStatusOracle, new ServiceRuntimeWrapper(loggerFactory), true)
        { }
    }

    /// <summary>
    ///  Stream queue balancer that uses Azure deployment information for load balancing. 
    /// Requires silo running in Azure.
    /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
    /// </summary>
    public class AzureDeploymentLeaseBasedBalancer : LeaseBasedQueueBalancer
    {
        public AzureDeploymentLeaseBasedBalancer(ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
            : base(serviceProvider, siloStatusOracle, new ServiceRuntimeWrapper(loggerFactory),
                  loggerFactory)
        { }
    }
}
