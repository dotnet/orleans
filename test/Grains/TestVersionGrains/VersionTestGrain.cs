using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.CodeGeneration;
using TestVersionGrainInterfaces;
using UnitTests.GrainInterfaces;

namespace TestVersionGrains
{
    public class VersionUpgradeTestGrain : Grain, IVersionUpgradeTestGrain
    {
        private ILogger logger;

        public override Task OnActivateAsync()
        {
            this.logger = this.ServiceProvider.GetService<ILoggerFactory>().CreateLogger($"VersionUpgradeTestGrain-{this.GetPrimaryKeyLong()}");
            this.logger.LogInformation("OnActivateAsync v1");
            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            this.logger.LogInformation("OnDeactivateAsync v1");
            return base.OnDeactivateAsync();
        }

        public Task<int> GetVersion()
        {
            return Task.FromResult(1);
        }

        public Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other)
        {
            return other.GetVersion();
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
            return Task.FromResult(1);
        }
    }
}
