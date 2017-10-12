using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;


namespace Orleans.Runtime
{
    internal class SiloControl : SystemTarget, ISiloControl
    {
        private readonly ILogger logger;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly Factory<NodeConfiguration> localConfiguration;
        private readonly ClusterConfiguration clusterConfiguration;

        private readonly DeploymentLoadPublisher deploymentLoadPublisher;
        private readonly Catalog catalog;
        private readonly GrainTypeManager grainTypeManager;
        private readonly ISiloPerformanceMetrics siloMetrics;
        private readonly ProviderManagerSystemTarget providerManagerSystemTarget;
        private readonly ICollection<IProviderManager> providerManagers;
        private readonly CachedVersionSelectorManager cachedVersionSelectorManager;
        private readonly CompatibilityDirectorManager compatibilityDirectorManager;
        private readonly VersionSelectorManager selectorManager;

        public SiloControl(
            ILocalSiloDetails localSiloDetails,
            Factory<NodeConfiguration> localConfiguration,
            ClusterConfiguration clusterConfiguration,
            DeploymentLoadPublisher deploymentLoadPublisher,
            Catalog catalog,
            GrainTypeManager grainTypeManager,
            ISiloPerformanceMetrics siloMetrics,
            IEnumerable<IProviderManager> providerManagers,
            ProviderManagerSystemTarget providerManagerSystemTarget,
            CachedVersionSelectorManager cachedVersionSelectorManager, 
            CompatibilityDirectorManager compatibilityDirectorManager,
            VersionSelectorManager selectorManager,
            ILoggerFactory loggerFactory)
            : base(Constants.SiloControlId, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.localSiloDetails = localSiloDetails;
            this.localConfiguration = localConfiguration;
            this.clusterConfiguration = clusterConfiguration;

            this.logger = loggerFactory.CreateLogger<SiloControl>();
            this.deploymentLoadPublisher = deploymentLoadPublisher;
            this.catalog = catalog;
            this.grainTypeManager = grainTypeManager;
            this.siloMetrics = siloMetrics;
            this.providerManagerSystemTarget = providerManagerSystemTarget;
            this.providerManagers = providerManagers.ToList();
            this.cachedVersionSelectorManager = cachedVersionSelectorManager;
            this.compatibilityDirectorManager = compatibilityDirectorManager;
            this.selectorManager = selectorManager;
        }

        #region Implementation of ISiloControl

        public Task Ping(string message)
        {
            logger.Info("Ping");
            return Task.CompletedTask;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect")]
        public Task ForceGarbageCollection()
        {
            logger.Info("ForceGarbageCollection");
            GC.Collect();
            return Task.CompletedTask;
        }

        public Task ForceActivationCollection(TimeSpan ageLimit)
        {
            logger.Info("ForceActivationCollection");
            return this.catalog.CollectActivations(ageLimit);
        }

        public Task ForceRuntimeStatisticsCollection()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("ForceRuntimeStatisticsCollection");
            return this.deploymentLoadPublisher.RefreshStatistics();
        }

        public Task<SiloRuntimeStatistics> GetRuntimeStatistics()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("GetRuntimeStatistics");
            return Task.FromResult(new SiloRuntimeStatistics(this.siloMetrics, DateTime.UtcNow));
        }

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics()
        {
            logger.Info("GetGrainStatistics");
            return Task.FromResult(this.catalog.GetGrainStatistics());
        }

        public Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[] types=null)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("GetDetailedGrainStatistics");
            return Task.FromResult(this.catalog.GetDetailedGrainStatistics(types));
        }

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics()
        {
            logger.Info("GetSimpleGrainStatistics");
            return Task.FromResult( this.catalog.GetSimpleGrainStatistics().Select(p =>
                new SimpleGrainStatistic { SiloAddress = this.localSiloDetails.SiloAddress, GrainType = p.Key, ActivationCount = (int)p.Value }).ToArray());
        }

        public Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId)
        {
            logger.Info("DetailedGrainReport for grain id {0}", grainId);
            return Task.FromResult( this.catalog.GetDetailedGrainReport(grainId));
        }

        public Task UpdateConfiguration(string configuration)
        {
            logger.Info("UpdateConfiguration with {0}", configuration);
            this.clusterConfiguration.Update(configuration);
            logger.Info(ErrorCode.Runtime_Error_100318, "UpdateConfiguration - new config is now {0}", this.clusterConfiguration.ToString(this.localSiloDetails.Name));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task UpdateStreamProviders(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations)
        {
            IStreamProviderManagerAgent streamProviderUpdateAgent =
                RuntimeClient.InternalGrainFactory.GetSystemTarget<IStreamProviderManagerAgent>(Constants.StreamProviderManagerAgentSystemTargetId, this.localSiloDetails.SiloAddress);

            await this.providerManagerSystemTarget.ScheduleTask(() => streamProviderUpdateAgent.UpdateStreamProviders(streamProviderConfigurations))
                .WithTimeout(TimeSpan.FromSeconds(25));
        }

        public Task<int> GetActivationCount()
        {
            return Task.FromResult(this.catalog.ActivationCount);
        }

        public Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg)
        {
            IProvider provider = null;
            foreach (var providerManager in this.providerManagers)
            {
                try
                {
                    var candidate = providerManager.GetProvider(providerName);
                    if (string.Equals(providerTypeFullName, candidate?.GetType()?.FullName))
                    {
                        provider = candidate;
                        break;
                    }
                }
                catch (Exception)
                {
                }
            }
            if (provider == null)
            {
                string error = $"Could not find provider for type {providerTypeFullName} and name {providerName}.";
                logger.Error(ErrorCode.Provider_ProviderNotFound, error);
                throw new ArgumentException(error);
            }

            IControllable controllable = provider as IControllable;
            if (controllable == null)
            {
                string error = $"The found provider of type {providerTypeFullName} and name {providerName} is not controllable.";
                logger.Error(ErrorCode.Provider_ProviderNotControllable, error);
                throw new ArgumentException(error);
            }
            return controllable.ExecuteCommand(command, arg);
        }

        public Task<string[]> GetGrainTypeList()
        {
            return Task.FromResult(this.grainTypeManager.GetGrainTypeList());
        }

        public Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            this.compatibilityDirectorManager.SetStrategy(strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            this.selectorManager.SetSelector(strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetCompatibilityStrategy(int interfaceId, CompatibilityStrategy strategy)
        {
            this.compatibilityDirectorManager.SetStrategy(interfaceId, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(int interfaceId, VersionSelectorStrategy strategy)
        {
            this.selectorManager.SetSelector(interfaceId, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        #endregion
    }
}
