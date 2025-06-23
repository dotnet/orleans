using System;
using System.Collections.Generic;
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
    /// A fake, test-only implementation of <see cref="IEnvironmentStatisticsProvider"/>.
    /// </summary>
    internal class TestHooksEnvironmentStatisticsProvider : IEnvironmentStatisticsProvider
    {
        private static EnvironmentStatisticsProvider _realStatisticsProvider = new();

        private EnvironmentStatistics? _currentStats = null;

        public EnvironmentStatistics GetEnvironmentStatistics()
        {
            var stats = _currentStats ?? new();
            if (!stats.IsValid())
            {
                stats = _realStatisticsProvider.GetEnvironmentStatistics();
            }

            return stats;
        }

        public void SetHardwareStatistics(EnvironmentStatistics stats) => _currentStats = stats;
    }

    /// <summary>
    /// Test hook functions for white box testing implemented as a SystemTarget
    /// </summary>
    internal sealed class TestHooksSystemTarget : SystemTarget, ITestHooksSystemTarget
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ISiloStatusOracle siloStatusOracle;

        private readonly TestHooksEnvironmentStatisticsProvider environmentStatistics;

        private readonly LoadSheddingOptions loadSheddingOptions;

        private readonly IConsistentRingProvider consistentRingProvider;

        public TestHooksSystemTarget(
            IServiceProvider serviceProvider,
            ILocalSiloDetails siloDetails,
            ILoggerFactory loggerFactory,
            ISiloStatusOracle siloStatusOracle,
            TestHooksEnvironmentStatisticsProvider environmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            SystemTargetShared shared)
            : base(Constants.TestHooksSystemTargetType, shared)
        {
            this.serviceProvider = serviceProvider;
            this.siloStatusOracle = siloStatusOracle;
            this.environmentStatistics = environmentStatistics;
            this.loadSheddingOptions = loadSheddingOptions.Value;
            this.consistentRingProvider = this.serviceProvider.GetRequiredService<IConsistentRingProvider>();
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public void Initialize()
        {
            // No-op
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
            return Task.FromResult(this.serviceProvider.GetKeyedService<IGrainStorage>(providerName) != null);
        }

        public Task<bool> HasStreamProvider(string providerName)
        {
            return Task.FromResult(this.serviceProvider.GetKeyedService<IGrainStorage>(providerName) != null);
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(this.serviceProvider.GetRequiredService<Catalog>().UnregisterGrainForTesting(grain));
        
        public Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod)
        {
            if (overloaded)
            {
                this.LatchCpuUsage(this.loadSheddingOptions.CpuThreshold + 1, latchPeriod);
            }
            else
            {
                this.LatchCpuUsage(this.loadSheddingOptions.CpuThreshold - 1, latchPeriod);
            }

            return Task.CompletedTask;
        }

        public Task<Dictionary<SiloAddress, SiloStatus>> GetApproximateSiloStatuses() => Task.FromResult(this.siloStatusOracle.GetApproximateSiloStatuses());

        private void LatchCpuUsage(float cpuUsage, TimeSpan latchPeriod)
        {
            var previousStats = environmentStatistics.GetEnvironmentStatistics();

            environmentStatistics.SetHardwareStatistics(
                new(cpuUsage, cpuUsage, previousStats.MemoryUsageBytes, previousStats.MemoryUsageBytes, previousStats.AvailableMemoryBytes, previousStats.AvailableMemoryBytes, previousStats.MaximumAvailableMemoryBytes));

            Task.Delay(latchPeriod).ContinueWith(t =>
                {
                    var currentStats = environmentStatistics.GetEnvironmentStatistics();

                    // ReSharper disable once CompareOfFloatsByEqualityOperator
                    if (currentStats.CpuUsagePercentage == cpuUsage)
                    {
                        environmentStatistics.SetHardwareStatistics(
                            new(previousStats.CpuUsagePercentage, previousStats.CpuUsagePercentage, currentStats.MemoryUsageBytes, currentStats.MemoryUsageBytes, currentStats.AvailableMemoryBytes, currentStats.AvailableMemoryBytes, currentStats.MaximumAvailableMemoryBytes));
                    }
                }).Ignore();
        }
    }
}
