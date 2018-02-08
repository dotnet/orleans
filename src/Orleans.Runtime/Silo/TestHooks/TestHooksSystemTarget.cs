using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Providers;
using Orleans.Runtime.ConsistentRing;
using Orleans.Storage;
using Orleans.Hosting;
using Orleans.Runtime.Counters;
using Orleans.Statistics;
using Orleans.Streams;

namespace Orleans.Runtime.TestHooks
{
    /// <summary>
    /// A fake, test-only implementation of <see cref="IHostEnvironmentStatistics"/>.
    /// </summary>
    public class TestHooksHostEnvironmentStatistics : IHostEnvironmentStatistics
    {
        /// <inheritdoc />
        public long? TotalPhysicalMemory { get; set; }

        /// <inheritdoc />
        public float? CpuUsage { get; set; }

        /// <inheritdoc />
        public long? AvailableMemory { get; set; }
    }

    /// <summary>
    /// Test hook functions for white box testing implemented as a SystemTarget
    /// </summary>
    internal class TestHooksSystemTarget : SystemTarget, ITestHooksSystemTarget
    {
        private readonly ISiloHost host;

        private readonly TestHooksHostEnvironmentStatistics hostEnvironmentStatistics;

        private readonly LoadSheddingOptions loadSheddingOptions;

        private readonly IConsistentRingProvider consistentRingProvider;

        public TestHooksSystemTarget(
            ISiloHost host,
            ILocalSiloDetails siloDetails,
            ILoggerFactory loggerFactory,
            TestHooksHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions)
            : base(Constants.TestHooksSystemTargetId, siloDetails.SiloAddress, loggerFactory)
        {
            this.host = host;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.loadSheddingOptions = loadSheddingOptions.Value;
            this.consistentRingProvider = this.host.Services.GetRequiredService<IConsistentRingProvider>();
        }

        public Task<SiloAddress> GetConsistentRingPrimaryTargetSilo(uint key)
        {
            return Task.FromResult(consistentRingProvider.GetPrimaryTargetSilo(key));
        }

        public Task<string> GetConsistentRingProviderDiagnosticInfo()
        {
            return Task.FromResult(consistentRingProvider.ToString()); 
        }

        public Task<bool> HasStatisticsProvider() => Task.FromResult(this.host.Services.GetServices<IStatisticsPublisher>() != null);

        public Task<Guid> GetServiceId() => Task.FromResult(this.host.Services.GetRequiredService<IOptions<SiloOptions>>().Value.ServiceId);

        public Task<bool> HasStorageProvider(string providerName)
        {
            return Task.FromResult(this.host.Services.GetServiceByName<IGrainStorage>(providerName) != null);
        }

        public Task<bool> HasStreamProvider(string providerName)
        {
            return Task.FromResult(this.host.Services.GetServiceByName<IGrainStorage>(providerName) != null);
        }

        public Task<ICollection<string>> GetStorageProviderNames()
        {
            var storageProviderCollection = this.host.Services.GetRequiredService<IKeyedServiceCollection<string, IGrainStorage>>();
            return Task.FromResult<ICollection<string>>(storageProviderCollection.GetServices(this.host.Services).Select(keyedService => keyedService.Key).ToArray());
        }

        public Task<ICollection<string>> GetStreamProviderNames()
        {
            var streamProviderCollection = this.host.Services.GetRequiredService<IKeyedServiceCollection<string, IStreamProvider>>();
            return Task.FromResult<ICollection<string>>(streamProviderCollection.GetServices(this.host.Services).Select(keyedService => keyedService.Key).ToArray());
        }

        public async Task<ICollection<string>> GetAllSiloProviderNames()
        {
            List<string> allProviders = new List<string>();

            allProviders.AddRange(await GetStorageProviderNames());

            allProviders.AddRange(await GetStorageProviderNames());

            var statisticsPublisherCollection = this.host.Services.GetRequiredService<IKeyedServiceCollection<string, IStatisticsPublisher>>(); ;
            allProviders.AddRange(statisticsPublisherCollection.GetServices(this.host.Services).Select(keyedService => keyedService.Key));

            return allProviders;
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(this.host.Services.GetRequiredService<Catalog>().UnregisterGrainForTesting(grain));
        
        public Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod)
        {
            if (overloaded)
            {
                this.hostEnvironmentStatistics.CpuUsage = this.loadSheddingOptions.LoadSheddingLimit + 1;
                Task.Delay(latchPeriod).ContinueWith(t => this.UnlatchIsOverloaded()).Ignore();
            }
            else
            {
                this.UnlatchIsOverloaded();
            }

            return Task.CompletedTask;
        }

        private void UnlatchIsOverloaded()
        {
            this.hostEnvironmentStatistics.CpuUsage = this.loadSheddingOptions.LoadSheddingLimit - 1;
        }
    }
}
