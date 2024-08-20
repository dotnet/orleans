using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal sealed class Catalog : SystemTarget, ICatalog
    {
        public SiloAddress LocalSilo { get; private set; }
        internal ISiloStatusOracle SiloStatusOracle { get; set; }
        private readonly ActivationCollector activationCollector;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly ActivationDirectory activations;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private readonly IOptions<GrainCollectionOptions> collectionOptions;
        private readonly GrainContextActivator grainActivator;

        public Catalog(
            ILocalSiloDetails localSiloDetails,
            GrainDirectoryResolver grainDirectoryResolver,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IOptions<GrainCollectionOptions> collectionOptions,
            GrainContextActivator grainActivator)
            : base(Constants.CatalogType, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.LocalSilo = localSiloDetails.SiloAddress;
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.activations = activationDirectory;
            this.serviceProvider = serviceProvider;
            this.collectionOptions = collectionOptions;
            this.grainActivator = grainActivator;
            this.logger = loggerFactory.CreateLogger<Catalog>();
            this.activationCollector = activationCollector;
            this.RuntimeClient = serviceProvider.GetRequiredService<InsideRuntimeClient>();

            GC.GetTotalMemory(true); // need to call once w/true to ensure false returns OK value

            MessagingProcessingInstruments.RegisterActivationDataAllObserve(() =>
            {
                long counter = 0;
                foreach (var activation in activations)
                {
                    if (activation.Value is ActivationData data)
                    {
                        counter += data.GetRequestCount();
                    }
                }

                return counter;
            });
            RegisterSystemTarget(this);
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
        /// If activation already exists, return it.
        /// Otherwise, creates a new activation, begins rehydrating it and activating it, then returns it.
        /// </summary>
        /// <remarks>
        /// There is no guarantee about the validity of the activation which is returned.
        /// Activations are responsible for handling any messages which they receive.
        /// </remarks>
        /// <param name="grainId">The grain identity.</param>
        /// <param name="requestContextData">Optional request context data.</param>
        /// <param name="rehydrationContext">Optional rehydration context.</param>
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
                    var address = GrainAddress.GetAddress(Silo, grainId, ActivationId.NewId());
                    result = this.grainActivator.CreateInstance(address);
                    activations.RecordNewTarget(result);
                }
            } // End lock

            if (result is null)
            {
                rehydrationContext?.Dispose();
                return UnableToCreateActivation(this, grainId);
            }

            CatalogInstruments.ActivationsCreated.Add(1);

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

                var grainLocator = self.serviceProvider.GetRequiredService<GrainLocator>();
                grainLocator.InvalidateCache(grainId);

                // Unregister the target activation so we don't keep getting spurious messages.
                // The time delay (one minute, as of this writing) is to handle the unlikely but possible race where
                // this request snuck ahead of another request, with new placement requested, for the same activation.
                // If the activation registration request from the new placement somehow sneaks ahead of this deregistration,
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
                var grainLocator = serviceProvider.GetRequiredService<GrainLocator>();
                await grainLocator.Unregister(address, UnregistrationCause.NonexistentActivation);
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
        private bool TryGetGrainContext(GrainId grainId, out IGrainContext data)
        {
            data = activations.FindTarget(grainId);
            return data != null;
        }

        /// <summary>
        /// Gracefully deletes activations, putting it into a shutdown state to
        /// complete and commit outstanding transactions before deleting it.
        /// To be called not from within Activation context, so can be awaited.
        /// </summary>
        internal async Task DeactivateActivations(DeactivationReason reason, List<IGrainContext> list, CancellationToken cancellationToken)
        {
            if (list == null || list.Count == 0) return;

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("DeactivateActivations: {Count} activations.", list.Count);
            var tasks = new List<Task>(list.Count);
            foreach (var activation in list)
            {
                activation.Deactivate(reason, cancellationToken);
                tasks.Add(activation.Deactivated);
            }

            await Task.WhenAll(tasks);
        }

        internal void StartDeactivatingActivations(DeactivationReason reason, List<IGrainContext> list, CancellationToken cancellationToken)
        {
            if (list == null || list.Count == 0) return;

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("DeactivateActivations: {Count} activations.", list.Count);

            foreach (var activation in list)
            {
                activation.Deactivate(reason, cancellationToken);
            }
        }

        public async Task DeactivateAllActivations(CancellationToken cancellationToken)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug((int)ErrorCode.Catalog_DeactivateAllActivations, "DeactivateAllActivations.");
            }

            var activationsToShutdown = new List<IGrainContext>();
            foreach (var pair in activations)
            {
                activationsToShutdown.Add(pair.Value);
            }

            var reason = new DeactivationReason(DeactivationReasonCode.ShuttingDown, "This process is terminating.");
            await DeactivateActivations(reason, activationsToShutdown, cancellationToken).WaitAsync(cancellationToken);
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
            var tasks = new List<Task>(addresses.Count);
            var deactivationReason = new DeactivationReason(reasonCode, reasonText);
            foreach (var activationAddress in addresses)
            {
                if (TryGetGrainContext(activationAddress.GrainId, out var grainContext))
                {
                    grainContext.Deactivate(deactivationReason);
                    tasks.Add(grainContext.Deactivated);
                }
            }

            return Task.WhenAll(tasks);
        }

        // TODO move this logic in the LocalGrainDirectory
        internal void OnSiloStatusChange(SiloAddress updatedSilo, SiloStatus status)
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
                var directory = serviceProvider.GetRequiredService<ILocalGrainDirectory>();
                lock (activations)
                {
                    foreach (var activation in activations)
                    {
                        try
                        {
                            var activationData = activation.Value;
                            var placementStrategy = activationData.GetComponent<PlacementStrategy>();
                            var isUsingGrainDirectory = placementStrategy is { IsUsingGrainDirectory: true };
                            if (!isUsingGrainDirectory || !grainDirectoryResolver.IsUsingDhtDirectory(activationData.GrainId.Type)) continue;
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
                    var reason = new DeactivationReason(DeactivationReasonCode.DirectoryFailure, reasonText);
                    StartDeactivatingActivations(reason, activationsToShutdown, CancellationToken.None);
                }
            }
        }
    }
}
