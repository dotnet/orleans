using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Storage;
using Orleans.Hosting;
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
        
        public Task<string> GetServiceId() => Task.FromResult(this.host.Services.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId);

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

            allProviders.AddRange(await GetStreamProviderNames());
            
            return allProviders;
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(this.host.Services.GetRequiredService<Catalog>().UnregisterGrainForTesting(grain));
        
        public Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod)
        {
            if (overloaded)
            {
                this.LatchCpuUsage(this.loadSheddingOptions.LoadSheddingLimit + 1, latchPeriod);
            }
            else
            {
                this.LatchCpuUsage(this.loadSheddingOptions.LoadSheddingLimit - 1, latchPeriod);
            }

            return Task.CompletedTask;
        }

        private void LatchCpuUsage(float? cpuUsage, TimeSpan latchPeriod)
        {
            var previousValue = this.hostEnvironmentStatistics.CpuUsage;
            this.hostEnvironmentStatistics.CpuUsage = cpuUsage;
            Task.Delay(latchPeriod).ContinueWith(t =>
                {
                    var currentCpuUsage = this.hostEnvironmentStatistics.CpuUsage;

                    // ReSharper disable once CompareOfFloatsByEqualityOperator
                    if (currentCpuUsage == cpuUsage)
                    {
                        this.hostEnvironmentStatistics.CpuUsage = previousValue;
                    }
                }).Ignore();
        }
    }
}
