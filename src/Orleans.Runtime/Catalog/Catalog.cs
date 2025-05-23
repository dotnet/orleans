using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime
{
    internal sealed partial class Catalog : SystemTarget, ICatalog, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly SiloAddress _siloAddress;
        private readonly ActivationCollector activationCollector;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly ActivationDirectory activations;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private readonly GrainContextActivator grainActivator;
        private ISiloStatusOracle _siloStatusOracle;

        public Catalog(
            ILocalSiloDetails localSiloDetails,
            GrainDirectoryResolver grainDirectoryResolver,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            GrainContextActivator grainActivator,
            SystemTargetShared shared)
            : base(Constants.CatalogType, shared)
        {
            this._siloAddress = localSiloDetails.SiloAddress;
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.activations = activationDirectory;
            this.serviceProvider = serviceProvider;
            this.grainActivator = grainActivator;
            this.logger = loggerFactory.CreateLogger<Catalog>();
            this.activationCollector = activationCollector;

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
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        /// <summary>
        /// Unregister message target and stop delivering messages to it
        /// </summary>
        /// <param name="activation"></param>
        public void UnregisterMessageTarget(IGrainContext activation)
        {
            if (activations.RemoveTarget(activation))
            {
                LogTraceUnregisteredActivation(activation);

                // this should be removed once we've refactored the deactivation code path. For now safe to keep.
                if (activation is ICollectibleGrainContext collectibleActivation)
                {
                    activationCollector.TryCancelCollection(collectibleActivation);
                }

                CatalogInstruments.ActivationsDestroyed.Add(1);
            }
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

                if (!_siloStatusOracle.CurrentStatus.IsTerminating())
                {
                    var address = new GrainAddress
                    {
                        SiloAddress = Silo,
                        GrainId = grainId,
                        ActivationId = ActivationId.NewId(),
                        MembershipVersion = MembershipVersion.MinValue,
                    };

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
            result.Activate(requestContextData);
            return result;

            [MethodImpl(MethodImplOptions.NoInlining)]
            static IGrainContext UnableToCreateActivation(Catalog self, GrainId grainId)
            {
                // Did not find and did not start placing new
                var isTerminating = self._siloStatusOracle.CurrentStatus.IsTerminating();
                if (isTerminating)
                {
                    self.LogDebugUnableToCreateActivationTerminating(grainId);
                }
                else
                {
                    self.LogDebugUnableToCreateActivation(grainId);
                }

                CatalogInstruments.NonExistentActivations.Add(1);

                var grainLocator = self.serviceProvider.GetRequiredService<GrainLocator>();
                grainLocator.InvalidateCache(grainId);
                if (!isTerminating)
                {
                    // Unregister the target activation so we don't keep getting spurious messages.
                    // The time delay (one minute, as of this writing) is to handle the unlikely but possible race where
                    // this request snuck ahead of another request, with new placement requested, for the same activation.
                    // If the activation registration request from the new placement somehow sneaks ahead of this deregistration,
                    // we want to make sure that we don't unregister the activation we just created.
                    var address = new GrainAddress { SiloAddress = self.Silo, GrainId = grainId };
                    _ = self.UnregisterNonExistentActivation(address);
                }

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
                LogFailedToUnregisterNonExistingActivation(address, exc);
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
        /// Gracefully deactivates activations, waiting for them to complete
        /// complete and commit outstanding transactions before deleting it.
        /// To be called not from within Activation context, so can be awaited.
        /// </summary>
        internal async Task DeactivateActivations(DeactivationReason reason, List<IGrainContext> list, CancellationToken cancellationToken)
        {
            if (list == null || list.Count == 0) return;

            LogDebugDeactivateActivations(list.Count);
            var options = new ParallelOptions
            {
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 512
            };
            await Parallel.ForEachAsync(list, options, (activation, _) =>
            {
                if (activation.GrainId.Type.IsSystemTarget())
                {
                    return ValueTask.CompletedTask;
                }

                activation.Deactivate(reason, cancellationToken);
                return new (activation.Deactivated);
            }).WaitAsync(cancellationToken);
        }

        public async Task DeactivateAllActivations(CancellationToken cancellationToken)
        {
            LogDebugDeactivateAllActivations();
            LogDebugDeactivateActivations(activations.Count);
            var reason = new DeactivationReason(DeactivationReasonCode.ShuttingDown, "This process is terminating.");
            var options = new ParallelOptions
            {
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 512
            };
            await Parallel.ForEachAsync(activations, options, (kv, _) =>
            {
                if (kv.Key.IsSystemTarget())
                {
                    return ValueTask.CompletedTask;
                }

                var activation = kv.Value;
                activation.Deactivate(reason, cancellationToken);
                return new (activation.Deactivated);
            }).WaitAsync(cancellationToken);
        }

        public async Task DeleteActivations(List<GrainAddress> addresses, DeactivationReasonCode reasonCode, string reasonText)
        {
            var tasks = new List<Task>(addresses.Count);
            var deactivationReason = new DeactivationReason(reasonCode, reasonText);
            await Parallel.ForEachAsync(addresses, (activationAddress, cancellationToken) =>
            {
                if (TryGetGrainContext(activationAddress.GrainId, out var grainContext))
                {
                    grainContext.Deactivate(deactivationReason);
                    return new ValueTask(grainContext.Deactivated);
                }

                return ValueTask.CompletedTask;
            });
        }

        // TODO move this logic in the LocalGrainDirectory
        internal void OnSiloStatusChange(ILocalGrainDirectory directory, SiloAddress updatedSilo, SiloStatus status)
        {
            // ignore joining events and also events on myself.
            if (updatedSilo.Equals(_siloAddress)) return;

            // We deactivate those activations when silo goes either of ShuttingDown/Stopping/Dead states,
            // since this is what Directory is doing as well. Directory removes a silo based on all those 3 statuses,
            // thus it will only deliver a "remove" notification for a given silo once to us. Therefore, we need to react the fist time we are notified.
            // We may review the directory behavior in the future and treat ShuttingDown differently ("drain only") and then this code will have to change a well.
            if (!status.IsTerminating()) return;
            if (status == SiloStatus.Dead)
            {
                this.RuntimeClient.BreakOutstandingMessagesToSilo(updatedSilo);
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
                            if (!isUsingGrainDirectory || !grainDirectoryResolver.IsUsingDefaultDirectory(activationData.GrainId.Type)) continue;
                            if (!updatedSilo.Equals(directory.GetPrimaryForGrain(activationData.GrainId))) continue;

                            activationsToShutdown.Add(activationData);
                        }
                        catch (Exception exc)
                        {
                            LogErrorCatalogSiloStatusChangeNotification(new(updatedSilo), exc);
                        }
                    }
                }

                if (activationsToShutdown.Count > 0)
                {
                    LogInfoCatalogSiloStatusChangeNotification(activationsToShutdown.Count, new(updatedSilo));
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

            void StartDeactivatingActivations(DeactivationReason reason, List<IGrainContext> list, CancellationToken cancellationToken)
            {
                if (list == null || list.Count == 0) return;

                LogDebugDeactivateActivations(list.Count);

                foreach (var activation in list)
                {
                    activation.Deactivate(reason, cancellationToken);
                }
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            // Do nothing, just ensure that this instance is created so that it can register itself in the activation directory.
            _siloStatusOracle = serviceProvider.GetRequiredService<ISiloStatusOracle>();
        }

        private readonly struct SiloAddressLogValue(SiloAddress silo)
        {
            public override string ToString() => silo.ToStringWithHashCode();
        }

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Catalog_SiloStatusChangeNotification_Exception,
            Message = "Catalog has thrown an exception while handling removal of silo {Silo}"
        )]
        private partial void LogErrorCatalogSiloStatusChangeNotification(SiloAddressLogValue silo, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.Catalog_SiloStatusChangeNotification,
            Message = "Catalog is deactivating {Count} activations due to a failure of silo {Silo}, since it is a primary directory partition to these grain ids."
        )]
        private partial void LogInfoCatalogSiloStatusChangeNotification(int count, SiloAddressLogValue silo);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Unregistered activation {Activation}")]
        private partial void LogTraceUnregisteredActivation(IGrainContext activation);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "DeactivateActivations: {Count} activations.")]
        private partial void LogDebugDeactivateActivations(int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.Catalog_DeactivateAllActivations,
            Message = "DeactivateAllActivations."
        )]
        private partial void LogDebugDeactivateAllActivations();

        [LoggerMessage(
            EventId = (int)ErrorCode.CatalogNonExistingActivation2,
            Level = LogLevel.Debug,
            Message = "Unable to create activation for grain {GrainId} because this silo is terminating")]
        private partial void LogDebugUnableToCreateActivationTerminating(GrainId grainId);

        [LoggerMessage(
            EventId = (int)ErrorCode.CatalogNonExistingActivation2,
            Level = LogLevel.Debug,
            Message = "Unable to create activation for grain {GrainId}"
        )]
        private partial void LogDebugUnableToCreateActivation(GrainId grainId);

        [LoggerMessage(
            EventId = (int)ErrorCode.Dispatcher_FailedToUnregisterNonExistingAct,
            Level = LogLevel.Warning,
            Message = "Failed to unregister non-existent activation {Address}"
        )]
        private partial void LogFailedToUnregisterNonExistingActivation(GrainAddress address, Exception exception);

    }
}
