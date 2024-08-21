#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Serialization.TypeSystem;
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
        private readonly IServiceProvider services;
        private readonly ActivationCollector _activationCollector;
        private readonly ActivationDirectory activationDirectory;

        private readonly IActivationWorkingSet activationWorkingSet;

        private readonly IEnvironmentStatisticsProvider environmentStatisticsProvider;

        private readonly IOptions<LoadSheddingOptions> loadSheddingOptions;
        private readonly GrainCountStatistics _grainCountStatistics;
        private readonly GrainPropertiesResolver grainPropertiesResolver;

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
            ActivationCollector activationCollector,
            ActivationDirectory activationDirectory,
            IActivationWorkingSet activationWorkingSet,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            GrainCountStatistics grainCountStatistics,
            GrainPropertiesResolver grainPropertiesResolver)
            : base(Constants.SiloControlType, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.localSiloDetails = localSiloDetails;

            this.logger = loggerFactory.CreateLogger<SiloControl>();
            this.deploymentLoadPublisher = deploymentLoadPublisher;
            this.catalog = catalog;
            this.cachedVersionSelectorManager = cachedVersionSelectorManager;
            this.compatibilityDirectorManager = compatibilityDirectorManager;
            this.selectorManager = selectorManager;
            this.services = services;
            _activationCollector = activationCollector;
            this.activationDirectory = activationDirectory;
            this.activationWorkingSet = activationWorkingSet;
            this.environmentStatisticsProvider = environmentStatisticsProvider;
            this.loadSheddingOptions = loadSheddingOptions;
            _grainCountStatistics = grainCountStatistics;
            this.grainPropertiesResolver = grainPropertiesResolver;
        }

        public Task Ping(string message)
        {
            logger.LogInformation("Ping");
            return Task.CompletedTask;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect")]
        public Task ForceGarbageCollection()
        {
            logger.LogInformation("ForceGarbageCollection");
            GC.Collect();
            return Task.CompletedTask;
        }

        public Task ForceActivationCollection(TimeSpan ageLimit)
        {
            logger.LogInformation("ForceActivationCollection");
            return _activationCollector.CollectActivations(ageLimit);
        }

        public Task ForceRuntimeStatisticsCollection()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("ForceRuntimeStatisticsCollection");
            return this.deploymentLoadPublisher.RefreshClusterStatistics();
        }

        public Task<SiloRuntimeStatistics> GetRuntimeStatistics()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("GetRuntimeStatistics");
            var activationCount = this.activationDirectory.Count;
            var stats = new SiloRuntimeStatistics(
                activationCount,
                activationWorkingSet.Count,
                this.environmentStatisticsProvider,
                this.loadSheddingOptions,
                DateTime.UtcNow);
            return Task.FromResult(stats);
        }

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics()
        {
            logger.LogInformation("GetGrainStatistics");
            var counts = new Dictionary<string, Dictionary<GrainId, int>>();
            lock (activationDirectory)
            {
                foreach (var activation in activationDirectory)
                {
                    var data = activation.Value;
                    if (data == null || data.GrainInstance == null) continue;

                    // TODO: generic type expansion
                    var grainTypeName = RuntimeTypeNameFormatter.Format(data.GrainInstance.GetType());

                    Dictionary<GrainId, int>? grains;
                    int n;
                    if (!counts.TryGetValue(grainTypeName, out grains))
                    {
                        counts.Add(grainTypeName, new Dictionary<GrainId, int> { { data.GrainId, 1 } });
                    }
                    else if (!grains.TryGetValue(data.GrainId, out n))
                        grains[data.GrainId] = 1;
                    else
                        grains[data.GrainId] = n + 1;
                }
            }

            return Task.FromResult(counts
                .SelectMany(p => p.Value.Select(p2 => Tuple.Create(p2.Key, p.Key, p2.Value)))
                .ToList());
        }

        public Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[]? types = null)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("GetDetailedGrainStatistics");
            var stats = new List<DetailedGrainStatistic>();
            lock (activationDirectory)
            {
                foreach (var activation in activationDirectory)
                {
                    var data = activation.Value;
                    if (data == null || data.GrainInstance == null) continue;

                    var grainType = RuntimeTypeNameFormatter.Format(data.GrainInstance.GetType());
                    if (types == null || types.Contains(grainType))
                    {
                        stats.Add(new DetailedGrainStatistic()
                        {
                            GrainType = grainType,
                            GrainId = data.GrainId,
                            SiloAddress = Silo
                        });
                    }
                }
            }

            return Task.FromResult(stats);
        }

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics()
        {
            logger.LogInformation("GetSimpleGrainStatistics");
            return Task.FromResult(_grainCountStatistics.GetSimpleGrainStatistics().Select(p =>
                new SimpleGrainStatistic { SiloAddress = this.localSiloDetails.SiloAddress, GrainType = p.Key, ActivationCount = (int)p.Value }).ToArray());
        }

        public Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId)
        {
            logger.LogInformation("DetailedGrainReport for grain id {GrainId}", grainId);
            string? grainClassName;
            try
            {
                var properties = this.grainPropertiesResolver.GetGrainProperties(grainId.Type);
                properties.Properties.TryGetValue(WellKnownGrainTypeProperties.TypeName, out grainClassName);
            }
            catch (Exception exc)
            {
                grainClassName = exc.ToString();
            }

            var activation = activationDirectory.FindTarget(grainId) switch
            {
                ActivationData data => data.ToDetailedString(),
                var a => a?.ToString()
            };

            var directory = services.GetRequiredService<ILocalGrainDirectory>();
            var report = new DetailedGrainReport()
            {
                Grain = grainId,
                SiloAddress = localSiloDetails.SiloAddress,
                SiloName = localSiloDetails.Name,
                LocalCacheActivationAddress = directory.GetLocalCacheData(grainId),
                LocalDirectoryActivationAddress = directory.GetLocalDirectoryData(grainId).Address,
                PrimaryForGrain = directory.GetPrimaryForGrain(grainId),
                GrainClassTypeName = grainClassName,
                LocalActivation = activation,
            };
            return Task.FromResult(report);
        }

        public Task<int> GetActivationCount()
        {
            return Task.FromResult(this.catalog.ActivationCount);
        }

        public Task<object> SendControlCommandToProvider<T>(string providerName, int command, object arg) where T : IControllable
        {
            var t = services
                    .GetKeyedServices<IControllable>(providerName);
            var controllable = services
                    .GetKeyedServices<IControllable>(providerName)
                    .FirstOrDefault(svc => svc.GetType() == typeof(T));

            if (controllable == null)
            {
                logger.LogError(
                    (int)ErrorCode.Provider_ProviderNotFound,
                    "Could not find a controllable service for type {ProviderTypeFullName} and name {ProviderName}.",
                    typeof(IControllable).FullName,
                    providerName);
                throw new ArgumentException($"Could not find a controllable service for type {typeof(IControllable).FullName} and name {providerName}.");
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

        public Task<List<GrainId>> GetActiveGrains(GrainType grainType)
        {
            var results = new List<GrainId>();
            foreach (var pair in activationDirectory)
            {
                if (grainType.Equals(pair.Key.Type))
                {
                    results.Add(pair.Key);
                }
            }
            return Task.FromResult(results);
        }
    }
}
