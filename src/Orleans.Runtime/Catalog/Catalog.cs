using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.TypeSystem;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    internal class Catalog : SystemTarget, ICatalog
    {
        public SiloAddress LocalSilo { get; private set; }
        internal ISiloStatusOracle SiloStatusOracle { get; set; }
        private readonly ActivationCollector activationCollector;
        private readonly GrainLocator grainLocator;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly ILocalGrainDirectory directory;
        private readonly ActivationDirectory activations;
        private IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private readonly string localSiloName;
        private readonly IOptions<GrainCollectionOptions> collectionOptions;
        private readonly GrainContextActivator grainActivator;
        private readonly GrainPropertiesResolver grainPropertiesResolver;
        public Catalog(
            ILocalSiloDetails localSiloDetails,
            GrainLocator grainLocator,
            GrainDirectoryResolver grainDirectoryResolver,
            ILocalGrainDirectory grainDirectory,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IOptions<GrainCollectionOptions> collectionOptions,
            GrainContextActivator grainActivator,
            GrainPropertiesResolver grainPropertiesResolver)
            : base(Constants.CatalogType, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.LocalSilo = localSiloDetails.SiloAddress;
            this.localSiloName = localSiloDetails.Name;
            this.grainLocator = grainLocator;
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.directory = grainDirectory;
            this.activations = activationDirectory;
            this.serviceProvider = serviceProvider;
            this.collectionOptions = collectionOptions;
            this.grainActivator = grainActivator;
            this.grainPropertiesResolver = grainPropertiesResolver;
            this.logger = loggerFactory.CreateLogger<Catalog>();
            this.activationCollector = activationCollector;
            this.RuntimeClient = serviceProvider.GetRequiredService<InsideRuntimeClient>();

            GC.GetTotalMemory(true); // need to call once w/true to ensure false returns OK value

            MessagingProcessingInstruments.RegisterActivationDataAllObserve(() =>
            {
                long counter = 0;
                lock (activations)
                {
                    foreach (var activation in activations)
                    {
                        if (activation.Value is ActivationData data)
                        {
                            counter += data.GetRequestCount();
                        }
                    }
                }
                return counter;
            });
            grainDirectory.SetSiloRemovedCatalogCallback(this.OnSiloStatusChange);
            RegisterSystemTarget(this);
        }

        public List<Tuple<GrainId, string, int>> GetGrainStatistics()
        {
            var counts = new Dictionary<string, Dictionary<GrainId, int>>();
            lock (activations)
            {
                foreach (var activation in activations)
                {
                    var data = activation.Value;
                    if (data == null || data.GrainInstance == null) continue;

                    // TODO: generic type expansion
                    var grainTypeName = RuntimeTypeNameFormatter.Format(data.GrainInstance.GetType());

                    Dictionary<GrainId, int> grains;
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
            return counts
                .SelectMany(p => p.Value.Select(p2 => Tuple.Create(p2.Key, p.Key, p2.Value)))
                .ToList();
        }

        public List<DetailedGrainStatistic> GetDetailedGrainStatistics(string[] types=null)
        {
            var stats = new List<DetailedGrainStatistic>();
            lock (activations)
            {
                foreach (var activation in activations)
                {
                    var data = activation.Value;
                    if (data == null || data.GrainInstance == null) continue;

                    var grainType = RuntimeTypeNameFormatter.Format(data.GrainInstance.GetType());
                    if (types==null || types.Contains(grainType))
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

        public DetailedGrainReport GetDetailedGrainReport(GrainId grain)
        {
            string grainClassName;
            try
            {
                var properties = this.grainPropertiesResolver.GetGrainProperties(grain.Type);
                properties.Properties.TryGetValue(WellKnownGrainTypeProperties.TypeName, out grainClassName);
            }
            catch (Exception exc)
            {
                grainClassName = exc.ToString();
            }

            var activation = activations.FindTarget(grain) switch
            {
                ActivationData data => data.ToDetailedString(),
                var a => a?.ToString()
            };

            return new()
            {
                Grain = grain,
                SiloAddress = LocalSilo,
                SiloName = localSiloName,
                LocalCacheActivationAddress = directory.GetLocalCacheData(grain),
                LocalDirectoryActivationAddress = directory.GetLocalDirectoryData(grain).Address,
                PrimaryForGrain = directory.GetPrimaryForGrain(grain),
                GrainClassTypeName = grainClassName,
                LocalActivation = activation,
            };
        }

        /// <summary>
        /// Register a new object to which messages can be delivered with the local lookup table and scheduler.
        /// </summary>
        /// <param name="activation"></param>
        public void RegisterMessageTarget(IGrainContext activation)
        {
            activations.RecordNewTarget(activation);
            CatalogInstruments.ActivationsCreated.Add(1);
        }

        /// <summary>
        /// Unregister message target and stop delivering messages to it
        /// </summary>
        /// <param name="activation"></param>
        public void UnregisterMessageTarget(IGrainContext activation)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Unregistering activation {Activation}", activation.ToString());
            }

            activations.RemoveTarget(activation);

            // this should be removed once we've refactored the deactivation code path. For now safe to keep.
            if (activation is ICollectibleGrainContext collectibleActivation)
            {
                activationCollector.TryCancelCollection(collectibleActivation);
            }

            CatalogInstruments.ActivationsDestroyed.Add(1);
        }

        /// <summary>
        /// FOR TESTING PURPOSES ONLY!!
        /// </summary>
        /// <param name="grain"></param>
        internal int UnregisterGrainForTesting(GrainId grain)
        {
            var activation = activations.FindTarget(grain);
            if (activation is null) return 0;

            UnregisterMessageTarget(activation);
            return 1;
        }

        public void RegisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            systemTarget.RuntimeClient = this.RuntimeClient;
            var sp = this.serviceProvider;
            systemTarget.WorkItemGroup = new WorkItemGroup(
                systemTarget,
                sp.GetRequiredService<ILogger<WorkItemGroup>>(),
                sp.GetRequiredService<ILogger<ActivationTaskScheduler>>(),
                sp.GetRequiredService<IOptions<SchedulingOptions>>());
            activations.RecordNewTarget(systemTarget);
        }

        public void UnregisterSystemTarget(ISystemTarget target)
        {
            var systemTarget = target as SystemTarget;
            if (systemTarget == null) throw new ArgumentException($"Parameter must be of type {typeof(SystemTarget)}", nameof(target));
            activations.RemoveTarget(systemTarget);
        }

        public int ActivationCount { get { return activations.Count; } }

        /// <summary>
        /// If activation already exists, use it
        /// Otherwise, create an activation of an existing grain by reading its state.
        /// Return immediately using a dummy that will queue messages.
        /// Concurrently start creating and initializing the real activation and replace it when it is ready.
        /// </summary>
        /// <param name="grainId">The grain identity</param>
        /// <param name="requestContextData">Request context data.</param>
        /// <returns></returns>
        public IGrainContext GetOrCreateActivation(
            in GrainId grainId,
            Dictionary<string, object> requestContextData,
            MigrationContext rehydrationContext)
        {
            if (TryGetGrainContext(grainId, out var result))
            {
                rehydrationContext?.Dispose();
                return result;
            }
            else if (grainId.IsSystemTarget())
            {
                rehydrationContext?.Dispose();
                return null;
            }

            // Lock over all activations to try to prevent multiple instances of the same activation being created concurrently.
            lock (activations)
            {
                if (TryGetGrainContext(grainId, out result))
                {
                    rehydrationContext?.Dispose();
                    return result;
                }

                if (!SiloStatusOracle.CurrentStatus.IsTerminating())
                {
                    var address = GrainAddress.GetAddress(Silo, grainId, new ActivationId(Guid.NewGuid()));
                    result = this.grainActivator.CreateInstance(address);
                    RegisterMessageTarget(result);
                }
            } // End lock

            if (result is null)
            {
                rehydrationContext?.Dispose();
                return UnableToCreateActivation(this, grainId);
            }

            // Rehydration occurs before activation.
            if (rehydrationContext is not null)
            {
                result.Rehydrate(rehydrationContext);
            }

            // Initialize the new activation asynchronously.
            var cancellation = new CancellationTokenSource(collectionOptions.Value.ActivationTimeout);
            result.Activate(requestContextData, cancellation.Token);
            return result;

            [MethodImpl(MethodImplOptions.NoInlining)]
            static IGrainContext UnableToCreateActivation(Catalog self, GrainId grainId)
            {
                // Did not find and did not start placing new
                if (self.logger.IsEnabled(LogLevel.Debug))
                {
                    if (self.SiloStatusOracle.CurrentStatus.IsTerminating())
                    {
                        self.logger.LogDebug((int)ErrorCode.CatalogNonExistingActivation2, "Unable to create activation for grain {GrainId} because this silo is terminating", grainId);
                    }
                    else
                    {
                        self.logger.LogDebug((int)ErrorCode.CatalogNonExistingActivation2, "Unable to create activation for grain {GrainId}", grainId);
                    }
                }

                CatalogInstruments.NonExistentActivations.Add(1);

                self.directory.InvalidateCacheEntry(grainId);

                // Unregister the target activation so we don't keep getting spurious messages.
                // The time delay (one minute, as of this writing) is to handle the unlikely but possible race where
                // this request snuck ahead of another request, with new placement requested, for the same activation.
                // If the activation registration request from the new placement somehow sneaks ahead of this unregistration,
                // we want to make sure that we don't unregister the activation we just created.
                var address = new GrainAddress { SiloAddress = self.Silo, GrainId = grainId };
                _ = self.UnregisterNonExistentActivation(address);
                return null;
            }
        }

        private async Task UnregisterNonExistentActivation(GrainAddress address)
        {
            try
            {
                await this.grainLocator.Unregister(address, UnregistrationCause.NonexistentActivation);
            }
            catch (Exception exc)
            {
                logger.LogWarning(
                    (int)ErrorCode.Dispatcher_FailedToUnregisterNonExistingAct,
                    exc,
                    "Failed to unregister non-existent activation {Address}",
                    address);
            }
        }

        /// <summary>
        /// Try to get runtime data for an activation
        /// </summary>
        public bool TryGetGrainContext(GrainId grainId, out IGrainContext data)
        {
            data = activations.FindTarget(grainId);
            return data != null;
        }

        /// <summary>
        /// Gracefully deletes activations, putting it into a shutdown state to
        /// complete and commit outstanding transactions before deleting it.
        /// To be called not from within Activation context, so can be awaited.
        /// </summary>
        internal async Task DeactivateActivations(DeactivationReason reason, List<IGrainContext> list)
        {
            if (list == null || list.Count == 0) return;

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("DeactivateActivations: {Count} activations.", list.Count);

            var timeoutTokenSource = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);
            await Task.WhenAll(list.Select(activation => activation.DeactivateAsync(reason, timeoutTokenSource.Token)));
        }

        internal void StartDeactivatingActivations(DeactivationReason reason, List<IGrainContext> list)
        {
            if (list == null || list.Count == 0) return;

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("DeactivateActivations: {Count} activations.", list.Count);

            var timeoutTokenSource = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);
            foreach (var activation in list)
            {
                activation.DeactivateAsync(reason, timeoutTokenSource.Token);
            }
        }

        public Task DeactivateAllActivations()
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug((int)ErrorCode.Catalog_DeactivateAllActivations, "DeactivateAllActivations.");
            }
            var activationsToShutdown = new List<IGrainContext>();
            foreach (var pair in activations)
            {
                var activation = pair.Value;
                if (activation is not ICollectibleGrainContext collectible || collectible.IsExemptFromCollection)
                {
                    continue;
                }

                activationsToShutdown.Add(activation);
            }

            var reason = new DeactivationReason(DeactivationReasonCode.ShuttingDown, "This process is terminating");
            return DeactivateActivations(reason, activationsToShutdown);
        }

        public SiloStatus LocalSiloStatus
        {
            get
            {
                return SiloStatusOracle.CurrentStatus;
            }
        }

        public Task DeleteActivations(List<GrainAddress> addresses, DeactivationReasonCode reasonCode, string reasonText)
        {
            var timeoutTokenSource = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);
            var tasks = new List<Task>(addresses.Count);
            var deactivationReason = new DeactivationReason(reasonCode, reasonText);
            foreach (var activationAddress in addresses)
            {
                if (TryGetGrainContext(activationAddress.GrainId, out var grainContext))
                {
                    tasks.Add(grainContext.DeactivateAsync(deactivationReason, timeoutTokenSource.Token));
                }
            }

            return Task.WhenAll(tasks);
        }

        // TODO move this logic in the LocalGrainDirectory
        private void OnSiloStatusChange(SiloAddress updatedSilo, SiloStatus status)
        {
            // ignore joining events and also events on myself.
            if (updatedSilo.Equals(LocalSilo)) return;

            // We deactivate those activations when silo goes either of ShuttingDown/Stopping/Dead states,
            // since this is what Directory is doing as well. Directory removes a silo based on all those 3 statuses,
            // thus it will only deliver a "remove" notification for a given silo once to us. Therefore, we need to react the fist time we are notified.
            // We may review the directory behavior in the future and treat ShuttingDown differently ("drain only") and then this code will have to change a well.
            if (!status.IsTerminating()) return;
            if (status == SiloStatus.Dead)
            {
                this.RuntimeClient.BreakOutstandingMessagesToDeadSilo(updatedSilo);
            }

            var activationsToShutdown = new List<IGrainContext>();
            try
            {
                // scan all activations in activation directory and deactivate the ones that the removed silo is their primary partition owner.
                lock (activations)
                {
                    foreach (var activation in activations)
                    {
                        try
                        {
                            var activationData = activation.Value;
                            var placementStrategy = activationData.GetComponent<PlacementStrategy>();
                            var isUsingGrainDirectory = placementStrategy is { IsUsingGrainDirectory: true };
                            if (!isUsingGrainDirectory || grainDirectoryResolver.HasNonDefaultDirectory(activationData.GrainId.Type)) continue;
                            if (!updatedSilo.Equals(directory.GetPrimaryForGrain(activationData.GrainId))) continue;

                            activationsToShutdown.Add(activationData);
                        }
                        catch (Exception exc)
                        {
                            logger.LogError(
                                (int)ErrorCode.Catalog_SiloStatusChangeNotification_Exception,
                                exc,
                               "Catalog has thrown an exception while handling removal of silo {Silo}", updatedSilo.ToStringWithHashCode());
                        }
                    }
                }

                if (activationsToShutdown.Count > 0)
                {
                    logger.LogInformation(
                        (int)ErrorCode.Catalog_SiloStatusChangeNotification,
                        "Catalog is deactivating {Count} activations due to a failure of silo {Silo}, since it is a primary directory partition to these grain ids.",
                        activationsToShutdown.Count,
                        updatedSilo.ToStringWithHashCode());
                }
            }
            finally
            {
                // outside the lock.
                if (activationsToShutdown.Count > 0)
                {
                    var reasonText = $"This activation is being deactivated due to a failure of server {updatedSilo}, since it was responsible for this activation's grain directory registration.";
                    var reason = new DeactivationReason(DeactivationReasonCode.InternalFailure, reasonText);
                    StartDeactivatingActivations(reason, activationsToShutdown);
                }
            }
        }

        public ValueTask AcceptMigratingGrains(List<GrainMigrationPackage> migratingGrains)
        {
            foreach (var package in migratingGrains)
            {
                // If the activation does not exist, create it and provide it with the migration context while doing so.
                // If the activation already exists or cannot be created, it is too late to perform migration, so ignore the request.
                GetOrCreateActivation(package.GrainId, requestContextData: null, package.MigrationContext);
            }

            return default;
        }
    }
}
