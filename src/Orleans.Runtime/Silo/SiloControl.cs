using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Statistics;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;


namespace Orleans.Runtime
{
    internal class SiloControl : SystemTarget, ISiloControl
    {
        private readonly ILogger logger;
        private readonly ILocalSiloDetails localSiloDetails;

        private readonly DeploymentLoadPublisher deploymentLoadPublisher;
        private readonly Catalog catalog;
        private readonly CachedVersionSelectorManager cachedVersionSelectorManager;
        private readonly CompatibilityDirectorManager compatibilityDirectorManager;
        private readonly VersionSelectorManager selectorManager;

        private readonly IMessageCenter messageCenter;

        private readonly ActivationDirectory activationDirectory;

        private readonly ActivationCollector activationCollector;

        private readonly IAppEnvironmentStatistics appEnvironmentStatistics;

        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;

        private readonly IOptions<LoadSheddingOptions> loadSheddingOptions;

        private readonly Dictionary<Tuple<string,string>, IControllable> controllables;

        public SiloControl(
            ILocalSiloDetails localSiloDetails,
            DeploymentLoadPublisher deploymentLoadPublisher,
            Catalog catalog,
            CachedVersionSelectorManager cachedVersionSelectorManager, 
            CompatibilityDirectorManager compatibilityDirectorManager,
            VersionSelectorManager selectorManager,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            IMessageCenter messageCenter,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions)
            : base(Constants.SiloControlType, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.localSiloDetails = localSiloDetails;

            this.logger = loggerFactory.CreateLogger<SiloControl>();
            this.deploymentLoadPublisher = deploymentLoadPublisher;
            this.catalog = catalog;
            this.cachedVersionSelectorManager = cachedVersionSelectorManager;
            this.compatibilityDirectorManager = compatibilityDirectorManager;
            this.selectorManager = selectorManager;
            this.messageCenter = messageCenter;
            this.activationDirectory = activationDirectory;
            this.activationCollector = activationCollector;
            this.appEnvironmentStatistics = appEnvironmentStatistics;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.loadSheddingOptions = loadSheddingOptions;
            this.controllables = new Dictionary<Tuple<string, string>, IControllable>();
            IEnumerable<IKeyedServiceCollection<string, IControllable>> namedIControllableCollections = services.GetServices<IKeyedServiceCollection<string, IControllable>>();
            foreach (IKeyedService<string, IControllable> keyedService in namedIControllableCollections.SelectMany(c => c.GetServices(services)))
            {
                IControllable controllable = keyedService.GetService(services);
                if(controllable != null)
                {
                    this.controllables.Add(Tuple.Create(controllable.GetType().FullName, keyedService.Key), controllable);
                }
            }

        }

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
            var activationCount = this.activationDirectory.Count;
            var recentlyUsedActivationCount = this.activationCollector.GetNumRecentlyUsed(TimeSpan.FromMinutes(10));
            var stats = new SiloRuntimeStatistics(
                activationCount,
                recentlyUsedActivationCount,
                this.appEnvironmentStatistics,
                this.hostEnvironmentStatistics,
                this.loadSheddingOptions,
                DateTime.UtcNow);
            return Task.FromResult(stats);
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

        public Task<int> GetActivationCount()
        {
            return Task.FromResult(this.catalog.ActivationCount);
        }

        public Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg)
        {
            IControllable controllable;
            if(!this.controllables.TryGetValue(Tuple.Create(providerTypeFullName, providerName), out controllable))
            {
                string error = $"Could not find a controllable service for type {providerTypeFullName} and name {providerName}.";
                logger.Error(ErrorCode.Provider_ProviderNotFound, error);
                throw new ArgumentException(error);
            }

            return controllable.ExecuteCommand(command, arg);
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

        public Task SetCompatibilityStrategy(GrainInterfaceType interfaceId, CompatibilityStrategy strategy)
        {
            this.compatibilityDirectorManager.SetStrategy(interfaceId, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy)
        {
            this.selectorManager.SetSelector(interfaceType, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }
    }
}
