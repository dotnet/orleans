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
    internal sealed partial class Catalog : SystemTarget, ICatalog, ILifecycleParticipant<ISiloLifecycle>, ISiloStatusListener
    {
        private readonly ActivationCollector activationCollector;
        private readonly ActivationDirectory activations;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private readonly GrainContextActivator grainActivator;
        private ISiloStatusOracle _siloStatusOracle;

        // Lock striping is used for activation creation to reduce contention
        private const int LockCount = 32; // Must be a power of 2
        private const int LockMask = LockCount - 1;
        private readonly object[] _locks = new object[LockCount];

        public Catalog(
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            GrainContextActivator grainActivator,
            SystemTargetShared shared)
            : base(Constants.CatalogType, shared)
        {
            this.activations = activationDirectory;
            this.serviceProvider = serviceProvider;
            this.grainActivator = grainActivator;
            this.logger = loggerFactory.CreateLogger<Catalog>();
            this.activationCollector = activationCollector;

            // Initialize lock striping array
            for (var i = 0; i < LockCount; i++)
            {
                _locks[i] = new object();
            }

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
        /// Gets the lock for a specific grain ID using consistent hashing.
        /// </summary>
        /// <param name="grainId">The grain ID to get the lock for.</param>
        /// <returns>The lock object for the specified grain ID.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object GetStripedLock(in GrainId grainId)
        {
            var hash = grainId.GetUniformHashCode();
            var lockIndex = (int)(hash & LockMask);
            return _locks[lockIndex];
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
                activationCollector.TryCancelCollection(activation as ICollectibleGrainContext);
                CatalogInstruments.ActivationsDestroyed.Add(1);
            }
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

            lock (GetStripedLock(grainId))
            {
                if (TryGetGrainContext(grainId, out result))
                {
                    rehydrationContext?.Dispose();
                    return result;
                }

                if (_siloStatusOracle.CurrentStatus == SiloStatus.Active)
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
                var status = self._siloStatusOracle.CurrentStatus;
                var isTerminating = status.IsTerminating();
                if (status is not SiloStatus.Active)
                {
                    self.LogDebugUnableToCreateActivationWhenNotActive(grainId);
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

        void ISiloStatusListener.SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (status == SiloStatus.Dead)
            {
                this.RuntimeClient.BreakOutstandingMessagesToSilo(updatedSilo);
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            // Do nothing, just ensure that this instance is created so that it can register itself in the activation directory.
            _siloStatusOracle = serviceProvider.GetRequiredService<ISiloStatusOracle>();
            _siloStatusOracle.SubscribeToSiloStatusEvents(this);
        }

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
            Level = LogLevel.Debug,
            Message = "Unable to create activation for grain {GrainId} because this silo is not active.")]
        private partial void LogDebugUnableToCreateActivationWhenNotActive(GrainId grainId);

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
