using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public interface IStreamQueueBalancerFactory
    {
        IStreamQueueBalancer Create(
            string strProviderName,
            ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration,
            IProviderRuntime runtime,
            IStreamQueueMapper queueMapper,
            TimeSpan siloMaturityPeriod);
    }
}
