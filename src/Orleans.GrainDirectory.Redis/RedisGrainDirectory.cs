using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.GrainDirectory.Redis
{
    public class RedisGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
    {
        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
        }

        public Task<GrainAddress> Lookup(string grainId)
        {
            throw new NotImplementedException();
        }

        public Task<GrainAddress> Register(GrainAddress address)
        {
            throw new NotImplementedException();
        }

        public Task Unregister(GrainAddress address)
        {
            throw new NotImplementedException();
        }

        public Task UnregisterSilos(List<string> siloAddresses)
        {
            throw new NotImplementedException();
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            throw new NotImplementedException();
        }
    }
}
