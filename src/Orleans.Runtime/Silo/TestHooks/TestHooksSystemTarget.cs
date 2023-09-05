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
using Orleans.Statistics;

namespace Orleans.Runtime.TestHooks
{
    /// <summary>
    /// A fake, test-only implementation of <see cref="IHostEnvironmentStatistics"/>.
    /// </summary>
    internal class TestHooksHostEnvironmentStatistics : IHostEnvironmentStatistics
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
        private readonly IServiceProvider serviceProvider;
        private readonly ISiloStatusOracle siloStatusOracle;

        private readonly TestHooksHostEnvironmentStatistics hostEnvironmentStatistics;

        private readonly LoadSheddingOptions loadSheddingOptions;

        private readonly IConsistentRingProvider consistentRingProvider;

        public TestHooksSystemTarget(
            IServiceProvider serviceProvider,
            ILocalSiloDetails siloDetails,
            ILoggerFactory loggerFactory,
            ISiloStatusOracle siloStatusOracle,
            TestHooksHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions)
            : base(Constants.TestHooksSystemTargetType, siloDetails.SiloAddress, loggerFactory)
        {
            this.serviceProvider = serviceProvider;
            this.siloStatusOracle = siloStatusOracle;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.loadSheddingOptions = loadSheddingOptions.Value;
            this.consistentRingProvider = this.serviceProvider.GetRequiredService<IConsistentRingProvider>();
        }

        public Task<SiloAddress> GetConsistentRingPrimaryTargetSilo(uint key)
        {
            return Task.FromResult(consistentRingProvider.GetPrimaryTargetSilo(key));
        }

        public Task<string> GetConsistentRingProviderDiagnosticInfo()
        {
            return Task.FromResult(consistentRingProvider.ToString()); 
        }
        
        public Task<string> GetServiceId() => Task.FromResult(this.serviceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId);

        public Task<bool> HasStorageProvider(string providerName)
        {
            return Task.FromResult(this.serviceProvider.GetServiceByName<IGrainStorage>(providerName) != null);
        }

        public Task<bool> HasStreamProvider(string providerName)
        {
            return Task.FromResult(this.serviceProvider.GetServiceByName<IGrainStorage>(providerName) != null);
        }

        public Task<ICollection<string>> GetStorageProviderNames()
        {
            var storageProviderCollection = this.serviceProvider.GetRequiredService<IKeyedServiceCollection<string, IGrainStorage>>();
            return Task.FromResult<ICollection<string>>(storageProviderCollection.GetServices(this.serviceProvider).Select(keyedService => keyedService.Key).ToArray());
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(this.serviceProvider.GetRequiredService<Catalog>().UnregisterGrainForTesting(grain));
        
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

        public Task<Dictionary<SiloAddress, SiloStatus>> GetApproximateSiloStatuses() => Task.FromResult(this.siloStatusOracle.GetApproximateSiloStatuses());

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
