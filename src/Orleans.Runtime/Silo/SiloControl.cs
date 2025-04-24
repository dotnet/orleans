using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Serialization.TypeSystem;
using Orleans.Statistics;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime
{
    internal sealed partial class SiloControl : SystemTarget, ISiloControl, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;
        private readonly ILocalSiloDetails localSiloDetails;

        private readonly DeploymentLoadPublisher deploymentLoadPublisher;
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
        private readonly GrainMigratabilityChecker _migratabilityChecker;

        public SiloControl(
            ILocalSiloDetails localSiloDetails,
            DeploymentLoadPublisher deploymentLoadPublisher,
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
            GrainPropertiesResolver grainPropertiesResolver,
            GrainMigratabilityChecker migratabilityChecker,
            SystemTargetShared shared)
            : base(Constants.SiloControlType, shared)
        {
            this.localSiloDetails = localSiloDetails;

            this.logger = loggerFactory.CreateLogger<SiloControl>();
            this.deploymentLoadPublisher = deploymentLoadPublisher;
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
            _migratabilityChecker = migratabilityChecker;
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public Task Ping(string message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInformationPing();
            return Task.CompletedTask;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect")]
        public Task ForceGarbageCollection(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInformationForceGarbageCollection();
            GC.Collect();
            return Task.CompletedTask;
        }

        public Task ForceActivationCollection(TimeSpan ageLimit, CancellationToken cancellationToken = default)
        {
            LogInformationForceActivationCollection();
            return _activationCollector.CollectActivations(ageLimit, cancellationToken);
        }

        public Task ForceRuntimeStatisticsCollection(CancellationToken cancellationToken = default)
        {
            LogDebugForceRuntimeStatisticsCollection();
            return this.deploymentLoadPublisher.RefreshClusterStatistics(cancellationToken);
        }

        public Task<SiloRuntimeStatistics> GetRuntimeStatistics(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDebugGetRuntimeStatistics();
            var activationCount = this.activationDirectory.Count;
            var stats = new SiloRuntimeStatistics(
                activationCount,
                activationWorkingSet.Count,
                this.environmentStatisticsProvider,
                this.loadSheddingOptions,
                DateTime.UtcNow);
            return Task.FromResult(stats);
        }

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInformationGetGrainStatistics();
            var counts = new Dictionary<string, Dictionary<GrainId, int>>();
            lock (activationDirectory)
            {
                foreach (var activation in activationDirectory)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

        public Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(
            string[]? types = null,
            CancellationToken cancellationToken = default)
        {
            var stats = GetDetailedGrainStatisticsCore(types, cancellationToken);
            return Task.FromResult(stats);
        }

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_grainCountStatistics.GetSimpleGrainStatistics().Select(p =>
                new SimpleGrainStatistic { SiloAddress = this.localSiloDetails.SiloAddress, GrainType = p.Key, ActivationCount = (int)p.Value }).ToArray());
        }

        public async Task<DetailedGrainReport> GetDetailedGrainReport(
            GrainId grainId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            var defaultDirectory = services.GetService<IGrainDirectory>();
            var dir = resolver.Resolve(grainId.Type) ?? defaultDirectory;
            GrainAddress? localCacheActivationAddress = null;
            GrainAddress? localDirectoryActivationAddress = null;
            SiloAddress? primaryForGrain = null;
            if (dir is DistributedGrainDirectory distributedGrainDirectory)
            {
                var grainLocator = services.GetRequiredService<GrainLocator>();
                grainLocator.TryLookupInCache(grainId, out localCacheActivationAddress);
                localDirectoryActivationAddress = await ((DistributedGrainDirectory.ITestHooks)distributedGrainDirectory)
                    .GetLocalRecord(grainId)
                    .WaitAsync(cancellationToken);
                primaryForGrain = ((DistributedGrainDirectory.ITestHooks)distributedGrainDirectory).GetPrimaryForGrain(grainId);
            }
            else if (dir is null && services.GetService<ILocalGrainDirectory>() is { } localGrainDirectory)
            {
                localCacheActivationAddress = localGrainDirectory.GetLocalCacheData(grainId);
                localDirectoryActivationAddress = localGrainDirectory.GetLocalDirectoryData(grainId).Address;
                primaryForGrain = localGrainDirectory.GetPrimaryForGrain(grainId);
            }

            var report = new DetailedGrainReport()
            {
                Grain = grainId,
                SiloAddress = localSiloDetails.SiloAddress,
                SiloName = localSiloDetails.Name,
                LocalCacheActivationAddress = localCacheActivationAddress,
                LocalDirectoryActivationAddress = localDirectoryActivationAddress,
                PrimaryForGrain = primaryForGrain,
                GrainClassTypeName = grainClassName,
                LocalActivation = activation,
            };

            return report;
        }

        public Task<int> GetActivationCount(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.activationDirectory.Count);
        }

        public Task<object?> SendControlCommandToProvider<T>(
            string providerName,
            int command,
            object? arg,
            CancellationToken cancellationToken = default) where T : IControllable
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = services
                    .GetKeyedServices<IControllable>(providerName);
            var controllable = services
                    .GetKeyedServices<IControllable>(providerName)
                    .FirstOrDefault(svc => svc.GetType() == typeof(T));

            if (controllable == null)
            {
                LogErrorProviderNotFound(typeof(IControllable).FullName!, providerName);
                throw new ArgumentException($"Could not find a controllable service for type {typeof(IControllable).FullName} and name {providerName}.");
            }

            return controllable.ExecuteCommand(command, arg).WaitAsync(cancellationToken);
        }

        public Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
            => SetCompatibilityStrategy(strategy, CancellationToken.None);

        public Task SetCompatibilityStrategy(
            CompatibilityStrategy strategy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.compatibilityDirectorManager.SetStrategy(strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(VersionSelectorStrategy strategy)
            => SetSelectorStrategy(strategy, CancellationToken.None);

        public Task SetSelectorStrategy(
            VersionSelectorStrategy strategy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.selectorManager.SetSelector(strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetCompatibilityStrategy(GrainInterfaceType interfaceId, CompatibilityStrategy strategy)
            => SetCompatibilityStrategy(interfaceId, strategy, CancellationToken.None);

        public Task SetCompatibilityStrategy(
            GrainInterfaceType interfaceId,
            CompatibilityStrategy strategy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.compatibilityDirectorManager.SetStrategy(interfaceId, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy)
            => SetSelectorStrategy(interfaceType, strategy, CancellationToken.None);

        public Task SetSelectorStrategy(
            GrainInterfaceType interfaceType,
            VersionSelectorStrategy strategy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.selectorManager.SetSelector(interfaceType, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task<List<GrainId>> GetActiveGrains(
            GrainType grainType,
            CancellationToken cancellationToken = default)
        {
            var results = new List<GrainId>();
            foreach (var pair in activationDirectory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (grainType.Equals(pair.Key.Type))
                {
                    results.Add(pair.Key);
                }
            }
            return Task.FromResult(results);
        }

        public Task MigrateRandomActivations(
            SiloAddress target,
            int count,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            var migrationContext = new Dictionary<string, object>()
            {
                [IPlacementDirector.PlacementHintKey] = target
            };

            // Loop until we've migrated the desired count of activations or run out of activations to try.
            // Note that we have a weak pseudorandom enumeration here, and lossy counting: this is not a precise
            // or deterministic operation.
            var remainingCount = count;
            foreach (var (grainId, grainContext) in activationDirectory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_migratabilityChecker.IsMigratable(grainId.Type, ImmovableKind.Rebalancer))
                {
                    continue;
                }

                if (--remainingCount <= 0)
                {
                    break;
                }

                grainContext.Migrate(migrationContext, cancellationToken);
            }

            return Task.CompletedTask;
        }

        private List<DetailedGrainStatistic> GetDetailedGrainStatisticsCore(
            string[]? types,
            CancellationToken cancellationToken)
        {
            var stats = new List<DetailedGrainStatistic>();
            lock (activationDirectory)
            {
                foreach (var activation in activationDirectory)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
            return stats;
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            // Do nothing, just ensure that this instance is created so that it can register itself in the activation directory.
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Ping"
        )]
        private partial void LogInformationPing();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ForceGarbageCollection"
        )]
        private partial void LogInformationForceGarbageCollection();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "ForceActivationCollection"
        )]
        private partial void LogInformationForceActivationCollection();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "ForceRuntimeStatisticsCollection"
        )]
        private partial void LogDebugForceRuntimeStatisticsCollection();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "GetRuntimeStatistics"
        )]
        private partial void LogDebugGetRuntimeStatistics();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "GetGrainStatistics"
        )]
        private partial void LogInformationGetGrainStatistics();

        [LoggerMessage(
            EventId = (int)ErrorCode.Provider_ProviderNotFound,
            Level = LogLevel.Error,
            Message = "Could not find a controllable service for type {ProviderTypeFullName} and name {ProviderName}."
        )]
        private partial void LogErrorProviderNotFound(string providerTypeFullName, string providerName);

        public Task DropDisconnectedClients(bool excludeRecent)
        {
            var gateway = this.services.GetRequiredService<MessageCenter>().Gateway;
            if (gateway is null)
            {
                // No gateway deployed on this silo.
                return Task.CompletedTask;
            }

            gateway.DropDisconnectedClients(excludeRecent);
            return Task.CompletedTask;
        }
    }
}
