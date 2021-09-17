using Distributed.GrainInterfaces.Ping;
using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Distributed.Grains.Ping
{
    public class PingRouterGrain : Grain, IPingRouterGrain
    {
        public async Task Ping(params Guid[] grainIds)
        {
            if (grainIds == null) throw new ArgumentNullException(nameof(grainIds));

            if (grainIds.Length == 0)
            {
                return;
            }

            var tasks = new List<Task>();
            foreach (var key in grainIds)
            {
                var grain = GrainFactory.GetGrain<IPingGrain>(key);
                tasks.Add(grain.Ping());
            }

            await Task.WhenAll(tasks);
        }
    }
}
