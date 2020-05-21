using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using TestVersionGrainInterfaces;
using UnitTests.GrainInterfaces;

namespace TestVersionGrains
{
    public class VersionUpgradeTestGrain : Grain, IVersionUpgradeTestGrain
    {
        public Task<int> GetVersion()
        {
            return Task.FromResult(2);
        }

        public async Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other)
        {
            var version = await other.GetVersion();
            return version;
        }

        public async Task<bool> LongRunningTask(TimeSpan taskTime)
        {
            await Task.Delay(taskTime);
            return true;
        }
    }

    [VersionAwareStrategy]
    public class VersionPlacementTestGrain : Grain, IVersionPlacementTestGrain
    {
        public Task<int> GetVersion()
        {
            return Task.FromResult(2);
        }
    }
}
