using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.GrainDirectory;
using Orleans.MultiCluster;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions;
using Orleans.Serialization;
using Orleans.Streams.Core;
using Orleans.Streams;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal class Catalog : SystemTarget, ICatalog, IPlacementRuntime
    {
        [Serializable]
        internal class NonExistentActivationException : Exception
        {
            public ActivationAddress NonExistentActivation { get; private set; }

            public bool IsStatelessWorker { get; private set; }

            public NonExistentActivationException() : base("NonExistentActivationException") { }
            public NonExistentActivationException(string msg) : base(msg) { }
            public NonExistentActivationException(string message, Exception innerException) 
                : base(message, innerException) { }

            public NonExistentActivationException(string msg, ActivationAddress nonExistentActivation, bool isStatelessWorker)
                : base(msg)
            {
                NonExistentActivation = nonExistentActivation;
                IsStatelessWorker = isStatelessWorker;
            }

            protected NonExistentActivationException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
                if (info != null)
                {
                    NonExistentActivation = (ActivationAddress)info.GetValue("NonExistentActivation", typeof(ActivationAddress));
                    IsStatelessWorker = (bool)info.GetValue("IsStatelessWorker", typeof(bool));
                }
            }

            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                if (info != null)
                {
                    info.AddValue("NonExistentActivation", NonExistentActivation, typeof(ActivationAddress));
                    info.AddValue("IsStatelessWorker", IsStatelessWorker, typeof(bool));
                }
                // MUST call through to the base class to let it save its own state
                base.GetObjectData(info, context);
            }
        }


        public GrainTypeManager GrainTypeManager { get; private set; }

        public SiloAddress LocalSilo { get; private set; }
        internal ISiloStatusOracle SiloStatusOracle { get; set; }
        private readonly ActivationCollector activationCollector;

        private readonly ILocalGrainDirectory directory;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ActivationDirectory activations;
        private IStreamProviderRuntime providerRuntime;
        private IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private int collectionNumber;
        private int destroyActivationsNumber;
        private IDisposable gcTimer;
        private readonly string localSiloName;
        private readonly CounterStatistic activationsCreated;
        private readonly CounterStatistic activationsDestroyed;
        private readonly CounterStatistic activationsFailedToActivate;
        private readonly IntValueStatistic inProcessRequests;
        private readonly CounterStatistic collectionCounter;
        private readonly GrainCreator grainCreator;
        private readonly TimeSpan maxRequestProcessingTime;
        private readonly TimeSpan maxWarningRequestProcessingTime;
        private readonly SerializationManager serializationManager;
        private readonly CachedVersionSelectorManager versionSelectorManager;
        private readonly ILoggerFactory loggerFactory;
        private readonly IOptions<GrainCollectionOptions> collectionOptions;
        private readonly IOptions<SiloMessagingOptions> messagingOptions;
        public Catalog(
            ILocalSiloDetails localSiloDetails,
            ILocalGrainDirectory grainDirectory,
            GrainTypeManager typeManager,
            OrleansTaskScheduler scheduler,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            GrainCreator grainCreator,
            ISiloMessageCenter messageCenter,
            PlacementDirectorsManager placementDirectorsManager,
            MessageFactory messageFactory,
            SerializationManager serializationManager,
            IStreamProviderRuntime providerRuntime,
            IServiceProvider serviceProvider,
            CachedVersionSelectorManager versionSelectorManager,
            ILoggerFactory loggerFactory,
            IOptions<SchedulingOptions> schedulingOptions,
            IOptions<GrainCollectionOptions> collectionOptions,
            IOptions<SiloMessagingOptions> messagingOptions)
            : base(Constants.CatalogId, messageCenter.MyAddress, loggerFactory)
        {
            this.LocalSilo = localSiloDetails.SiloAddress;
            this.localSiloName = localSiloDetails.Name;
            this.directory = grainDirectory;
            this.activations = activationDirectory;
            this.scheduler = scheduler;
            this.loggerFactory = loggerFactory;
            this.GrainTypeManager = typeManager;
            this.collectionNumber = 0;
            this.destroyActivationsNumber = 0;
            this.grainCreator = grainCreator;
            this.serializationManager = serializationManager;
            this.versionSelectorManager = versionSelectorManager;
            this.providerRuntime = providerRuntime;
            this.serviceProvider = serviceProvider;
            this.collectionOptions = collectionOptions;
            this.messagingOptions = messagingOptions;
            this.logger = loggerFactory.CreateLogger<Catalog>();
            this.activationCollector = activationCollector;
            this.Dispatcher = new Dispatcher(
                scheduler,
                messageCenter,
                this,
                this.messagingOptions,
                placementDirectorsManager,
                grainDirectory,
                this.activationCollector,
                messageFactory,
                serializationManager,
                versionSelectorManager.CompatibilityDirectorManager,
                loggerFactory,
                schedulingOptions);
            GC.GetTotalMemory(true); // need to call once w/true to ensure false returns OK value

// TODO: figure out how to read config change notification from options. - jbragg
//            config.OnConfigChange("Globals/Activation", () => scheduler.RunOrQueueAction(Start, SchedulingContext), false);
            IntValueStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_COUNT, () => activations.Count);
            activationsCreated = CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_CREATED);
            activationsDestroyed = CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_DESTROYED);
            activationsFailedToActivate = CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE);
            collectionCounter = CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS);
            inProcessRequests = IntValueStatistic.FindOrCreate(StatisticNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL, () =>
            {
                long counter = 0;
                lock (activations)
                {
                    foreach (var activation in activations)
                    {
                        ActivationData data = activation.Value;
                        counter += data.GetRequestCount();
                    }
                }
                return counter;
            });
            maxWarningRequestProcessingTime = this.messagingOptions.Value.ResponseTimeout.Multiply(5);
            maxRequestProcessingTime = this.messagingOptions.Value.MaxRequestProcessingTime;
            grainDirectory.SetSiloRemovedCatalogCallback(this.OnSiloStatusChange);
        }

        /// <summary>
        /// Gets the dispatcher used by this instance.
        /// </summary>
        public Dispatcher Dispatcher { get; }

        public IList<SiloAddress> GetCompatibleSilos(PlacementTarget target)
        {
            // For test only: if we have silos that are not yet in the Cluster TypeMap, we assume that they are compatible
            // with the current silo
            if (this.messagingOptions.Value.AssumeHomogenousSilosForTesting)
                return AllActiveSilos;

            var typeCode = target.GrainIdentity.TypeCode;
            var silos = target.InterfaceVersion > 0
                ? versionSelectorManager.GetSuitableSilos(typeCode, target.InterfaceId, target.InterfaceVersion).SuitableSilos
                : GrainTypeManager.GetSupportedSilos(typeCode);

            var compatibleSilos = silos.Intersect(AllActiveSilos).ToList();
            if (compatibleSilos.Count == 0)
                throw new OrleansException($"TypeCode ${typeCode} not supported in the cluster");

            return compatibleSilos;
        }

        public IReadOnlyDictionary<ushort, IReadOnlyList<SiloAddress>> GetCompatibleSilosWithVersions(PlacementTarget target)
        {
            if (target.InterfaceVersion == 0)
                throw new ArgumentException("Interface version not provided", nameof(target));

            var typeCode = target.GrainIdentity.TypeCode;
            var silos = versionSelectorManager
                .GetSuitableSilos(typeCode, target.InterfaceId, target.InterfaceVersion)
                .SuitableSilosByVersion;

            return silos;
        }

        internal void Start()
        {
            if (gcTimer != null) gcTimer.Dispose();

            var t = GrainTimer.FromTaskCallback(
                this.RuntimeClient.Scheduler,
                this.loggerFactory.CreateLogger<GrainTimer>(),
                OnTimer,
                null,
                TimeSpan.Zero,
                this.activationCollector.Quantum,
                "Catalog.GCTimer");
            t.Start();
            gcTimer = t;
        }

        private Task OnTimer(object _)
        {
            return CollectActivationsImpl(true);
        }

        public Task CollectActivations(TimeSpan ageLimit)
        {
            return CollectActivationsImpl(false, ageLimit);
        }

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit = default(TimeSpan))
        {
            var watch = new Stopwatch();
            watch.Start();
            var number = Interlocked.Increment(ref collectionNumber);
            long memBefore = GC.GetTotalMemory(false) / (1024 * 1024);
            logger.Info(ErrorCode.Catalog_BeforeCollection, "Before collection#{0}: memory={1}MB, #activations={2}, collector={3}.",
                number, memBefore, activations.Count, this.activationCollector.ToString());
            List<ActivationData> list = scanStale ? this.activationCollector.ScanStale() : this.activationCollector.ScanAll(ageLimit);
            collectionCounter.Increment();
            var count = 0;
            if (list != null && list.Count > 0)
            {
                count = list.Count;
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("CollectActivations{0}", list.ToStrings(d => d.Grain.ToString() + d.ActivationId));
                await DeactivateActivationsFromCollector(list);
            }
            long memAfter = GC.GetTotalMemory(false) / (1024 * 1024);
            watch.Stop();
            logger.Info(ErrorCode.Catalog_AfterCollection, "After collection#{0}: memory={1}MB, #activations={2}, collected {3} activations, collector={4}, collection time={5}.",
                number, memAfter, activations.Count, count, this.activationCollector.ToString(), watch.Elapsed);
        }

        public List<Tuple<GrainId, string, int>> GetGrainStatistics()
        {
            var counts = new Dictionary<string, Dictionary<GrainId, int>>();
            lock (activations)
            {
                foreach (var activation in activations)
                {
                    ActivationData data = activation.Value;
                    if (data == null || data.GrainInstance == null) continue;

                    // TODO: generic type expansion
                    var grainTypeName = TypeUtils.GetFullName(data.GrainInstanceType);
                    
                    Dictionary<GrainId, int> grains;
                    int n;
                    if (!counts.TryGetValue(grainTypeName, out grains))
                    {
                        counts.Add(grainTypeName, new Dictionary<GrainId, int> { { data.Grain, 1 } });
                    }
                    else if (!grains.TryGetValue(data.Grain, out n))
                        grains[data.Grain] = 1;
                    else
                        grains[data.Grain] = n + 1;
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
                    ActivationData data = activation.Value;
                    if (data == null || data.GrainInstance == null) continue;

                    if (types==null || types.Contains(TypeUtils.GetFullName(data.GrainInstanceType)))
                    {
                        stats.Add(new DetailedGrainStatistic()
                        {
                            GrainType = TypeUtils.GetFullName(data.GrainInstanceType),
                            GrainIdentity = data.Grain,
                            SiloAddress = data.Silo,
                            Category = data.Grain.Category.ToString()
                        });
                    }
                }
            }
            return stats;
        }

        public IEnumerable<KeyValuePair<string, long>> GetSimpleGrainStatistics()
        {
            return activations.GetSimpleGrainStatistics();
        }

        public DetailedGrainReport GetDetailedGrainReport(GrainId grain)
        {
            var report = new DetailedGrainReport
            {
                Grain = grain,
                SiloAddress = LocalSilo,
                SiloName = localSiloName,
                LocalCacheActivationAddresses = directory.GetLocalCacheData(grain),
                LocalDirectoryActivationAddresses = directory.GetLocalDirectoryData(grain).Addresses,
                PrimaryForGrain = directory.GetPrimaryForGrain(grain)
            };
            try
            {
                PlacementStrategy unused;
                MultiClusterRegistrationStrategy unusedActivationStrategy;
                string grainClassName;
                GrainTypeManager.GetTypeInfo(grain.TypeCode, out grainClassName, out unused, out unusedActivationStrategy);
                report.GrainClassTypeName = grainClassName;
            }
            catch (Exception exc)
            {
                report.GrainClassTypeName = exc.ToString();
            }

            List<ActivationData> acts = activations.FindTargets(grain);
            report.LocalActivations = acts != null ? 
                acts.Select(activationData => activationData.ToDetailedString()).ToList() : 
                new List<string>();
            return report;
        }

#region MessageTargets

        /// <summary>
        /// Register a new object to which messages can be delivered with the local lookup table and scheduler.
        /// </summary>
        /// <param name="activation"></param>
        public void RegisterMessageTarget(ActivationData activation)
        {
            scheduler.RegisterWorkContext(activation.SchedulingContext);
            activations.RecordNewTarget(activation);
            activationsCreated.Increment();
        }

        /// <summary>
        /// Unregister message target and stop delivering messages to it
        /// </summary>
        /// <param name="activation"></param>
        public void UnregisterMessageTarget(ActivationData activation)
        {
            activations.RemoveTarget(activation);

            // this should be removed once we've refactored the deactivation code path. For now safe to keep.
            this.activationCollector.TryCancelCollection(activation);
            activationsDestroyed.Increment();

            scheduler.UnregisterWorkContext(activation.SchedulingContext);

            if (activation.GrainInstance == null) return;

            var grainTypeName = TypeUtils.GetFullName(activation.GrainInstanceType);
            activations.DecrementGrainCounter(grainTypeName);
            activation.SetGrainInstance(null);
        }

        /// <summary>
        /// FOR TESTING PURPOSES ONLY!!
        /// </summary>
        /// <param name="grain"></param>
        internal int UnregisterGrainForTesting(GrainId grain)
        {
            var acts = activations.FindTargets(grain);
            if (acts == null) return 0;

            int numActsBefore = acts.Count;
            foreach (var act in acts)
                UnregisterMessageTarget(act);
            
            return numActsBefore;
        }

#endregion

#region Grains

        internal bool CanInterleave(ActivationId running, Message message)
        {
            ActivationData target;
            GrainTypeData data;
            return TryGetActivationData(running, out target) &&
                target.GrainInstance != null &&
                GrainTypeManager.TryGetData(TypeUtils.GetFullName(target.GrainInstanceType), out data) &&
                (data.IsReentrant || data.MayInterleave((InvokeMethodRequest)message.GetDeserializedBody(this.serializationManager)));
        }

        public void GetGrainTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, out MultiClusterRegistrationStrategy activationStrategy, string genericArguments = null)
        {
            GrainTypeManager.GetTypeInfo(typeCode, out grainClass, out placement, out activationStrategy, genericArguments);
        }

#endregion

#region Activations

        public int ActivationCount { get { return activations.Count; } }

        /// <summary>
        /// If activation already exists, use it
        /// Otherwise, create an activation of an existing grain by reading its state.
        /// Return immediately using a dummy that will queue messages.
        /// Concurrently start creating and initializing the real activation and replace it when it is ready.
        /// </summary>
        /// <param name="address">Grain's activation address</param>
        /// <param name="newPlacement">Creation of new activation was requested by the placement director.</param>
        /// <param name="grainType">The type of grain to be activated or created</param>
        /// <param name="genericArguments">Specific generic type of grain to be activated or created</param>
        /// <param name="requestContextData">Request context data.</param>
        /// <param name="activatedPromise"></param>
        /// <returns></returns>
        public ActivationData GetOrCreateActivation(
            ActivationAddress address,
            bool newPlacement,
            string grainType,
            string genericArguments,
            Dictionary<string, object> requestContextData,
            out Task activatedPromise)
        {
            ActivationData result;
            activatedPromise = Task.CompletedTask;
            PlacementStrategy placement;

            lock (activations)
            {
                if (TryGetActivationData(address.Activation, out result))
                {
                    return result;
                }
                
                int typeCode = address.Grain.TypeCode;
                string actualGrainType = null;
                MultiClusterRegistrationStrategy activationStrategy;

                if (typeCode != 0)
                {
                    GetGrainTypeInfo(typeCode, out actualGrainType, out placement, out activationStrategy, genericArguments);
                    if (string.IsNullOrEmpty(grainType))
                    {
                        grainType = actualGrainType;
                    }
                }
                else
                {
                    // special case for Membership grain.
                    placement = SystemPlacement.Singleton;
                    activationStrategy = ClusterLocalRegistration.Singleton;
                }

                if (newPlacement && !SiloStatusOracle.CurrentStatus.IsTerminating())
                {
                    TimeSpan ageLimit = this.collectionOptions.Value.ClassSpecificCollectionAge.TryGetValue(grainType, out TimeSpan limit)
                        ? limit
                        : collectionOptions.Value.CollectionAge;

                    // create a dummy activation that will queue up messages until the real data arrives
                    // We want to do this (RegisterMessageTarget) under the same lock that we tested TryGetActivationData. They both access ActivationDirectory.
                    result = new ActivationData(
                        address, 
                        genericArguments, 
                        placement, 
                        activationStrategy,
                        this.activationCollector,
                        ageLimit,
                        this.messagingOptions,
                        this.maxWarningRequestProcessingTime,
                        this.maxRequestProcessingTime,
                        this.RuntimeClient,
                        this.loggerFactory);
                    RegisterMessageTarget(result);
                }
            } // End lock

            // Did not find and did not start placing new
            if (result == null)
            {
                var msg = String.Format("Non-existent activation: {0}, grain type: {1}.",
                                           address.ToFullString(), grainType);
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.CatalogNonExistingActivation2, msg);
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS).Increment();
                throw new NonExistentActivationException(msg, address, placement is StatelessWorkerPlacement);
            }
   
            SetupActivationInstance(result, grainType, genericArguments);
            activatedPromise = InitActivation(result, grainType, genericArguments, requestContextData);
            return result;
        }

        private void SetupActivationInstance(ActivationData result, string grainType, string genericArguments)
        {
            lock (result)
            {
                if (result.GrainInstance == null)
                {
                    CreateGrainInstance(grainType, result, genericArguments);
                }
            }
        }

        private enum ActivationInitializationStage
        {
            None,
            Register,
            SetupState,
            InvokeActivate,
            Completed
        }

        private async Task InitActivation(ActivationData activation, string grainType, string genericArguments,
            Dictionary<string, object> requestContextData)
        {
            // We've created a dummy activation, which we'll eventually return, but in the meantime we'll queue up (or perform promptly)
            // the operations required to turn the "dummy" activation into a real activation
            var initStage = ActivationInitializationStage.None;

            // A chain of promises that will have to complete in order to complete the activation
            // Register with the grain directory, register with the store if necessary and call the Activate method on the new activation.
            try
            {
                initStage = ActivationInitializationStage.Register;
                var registrationResult = await RegisterActivationInGrainDirectoryAndValidate(activation);
                if (!registrationResult.IsSuccess)
                {
                    // If registration failed, recover and bail out.
                    RecoverFailedInitActivation(activation, initStage, registrationResult);
                    return;
                }

                initStage = ActivationInitializationStage.SetupState;

                initStage = ActivationInitializationStage.InvokeActivate;
                await InvokeActivate(activation, requestContextData);

                this.activationCollector.ScheduleCollection(activation);

                // Success!! Log the result, and start processing messages
                initStage = ActivationInitializationStage.Completed;
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("InitActivation is done: {0}", activation.Address);
            }
            catch (Exception ex)
            {
                RecoverFailedInitActivation(activation, initStage, exception: ex);
                throw;
            }
        }

        /// <summary>
        /// Recover from a failed attempt to initialize a new activation.
        /// </summary>
        /// <param name="activation">The activation which failed to be initialized.</param>
        /// <param name="initStage">The initialization stage at which initialization failed.</param>
        /// <param name="registrationResult">The result of registering the activation with the grain directory.</param>
        /// <param name="exception">The exception, if present, for logging purposes.</param>
        private void RecoverFailedInitActivation(
            ActivationData activation,
            ActivationInitializationStage initStage,
            ActivationRegistrationResult registrationResult = default(ActivationRegistrationResult),
            Exception exception = null)
        {
            ActivationAddress address = activation.Address;
            lock (activation)
            {
                activation.SetState(ActivationState.Invalid);
                try
                {
                    UnregisterMessageTarget(activation);
                }
                catch (Exception exc)
                {
                    logger.Warn(ErrorCode.Catalog_UnregisterMessageTarget4, $"UnregisterMessageTarget failed on {activation}.", exc);
                }

                switch (initStage)
                {
                    case ActivationInitializationStage.Register: // failed to RegisterActivationInGrainDirectory

                        // Failure!! Could it be that this grain uses single activation placement, and there already was an activation?
                        // If the registration result is not set, the forwarding address will be null.
                        activation.ForwardingAddress = registrationResult.ExistingActivationAddress;
                        if (activation.ForwardingAddress != null)
                        {
                            CounterStatistic
                                .FindOrCreate(StatisticNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS)
                                .Increment();
                            var primary = directory.GetPrimaryForGrain(activation.ForwardingAddress.Grain);
                            if (logger.IsEnabled(LogLevel.Information))
                            {
                                // If this was a duplicate, it's not an error, just a race.
                                // Forward on all of the pending messages, and then forget about this activation.
                                var logMsg =
                                    $"Tried to create a duplicate activation {address}, but we'll use {activation.ForwardingAddress} instead. " +
                                    $"GrainInstanceType is {activation.GrainInstanceType}. " +
                                    $"{(primary != null ? "Primary Directory partition for this grain is " + primary + ". " : string.Empty)}" +
                                    $"Full activation address is {address.ToFullString()}. We have {activation.WaitingCount} messages to forward.";
                                if (activation.IsUsingGrainDirectory)
                                {
                                    logger.Info(ErrorCode.Catalog_DuplicateActivation, logMsg);
                                }
                                else
                                {
                                    logger.Debug(ErrorCode.Catalog_DuplicateActivation, logMsg);
                                }
                            }

                            RerouteAllQueuedMessages(activation, activation.ForwardingAddress, "Duplicate activation", exception);
                        }
                        else
                        {
                            logger.Warn(ErrorCode.Runtime_Error_100064,
                                $"Failed to RegisterActivationInGrainDirectory for {activation}.", exception);
                            // Need to undo the registration we just did earlier
                            if (activation.IsUsingGrainDirectory)
                            {
                                scheduler.RunOrQueueTask(
                                    () => directory.UnregisterAsync(address, UnregistrationCause.Force),
                                    SchedulingContext).Ignore();
                            }
                            RerouteAllQueuedMessages(activation, null,
                                "Failed RegisterActivationInGrainDirectory", exception);
                        }
                        break;

                    case ActivationInitializationStage.SetupState: // failed to setup persistent state

                        logger.Warn(ErrorCode.Catalog_Failed_SetupActivationState,
                            string.Format("Failed to SetupActivationState for {0}.", activation), exception);
                        // Need to undo the registration we just did earlier
                        if (activation.IsUsingGrainDirectory)
                        {
                            scheduler.RunOrQueueTask(
                                () => directory.UnregisterAsync(address, UnregistrationCause.Force),
                                SchedulingContext).Ignore();
                        }

                        RerouteAllQueuedMessages(activation, null, "Failed SetupActivationState", exception);
                        break;

                    case ActivationInitializationStage.InvokeActivate: // failed to InvokeActivate

                        logger.Warn(ErrorCode.Catalog_Failed_InvokeActivate,
                            string.Format("Failed to InvokeActivate for {0}.", activation), exception);
                        // Need to undo the registration we just did earlier
                        if (activation.IsUsingGrainDirectory)
                        {
                            scheduler.RunOrQueueTask(
                                () => directory.UnregisterAsync(address, UnregistrationCause.Force),
                                SchedulingContext).Ignore();
                        }

                        // Reject all of the messages queued for this activation.
                        var activationFailedMsg = nameof(Grain.OnActivateAsync) + " failed";
                        RejectAllQueuedMessages(activation, activationFailedMsg, exception);
                        break;
                }
            }
        }

        /// <summary>
        /// Perform just the prompt, local part of creating an activation object
        /// Caller is responsible for registering locally, registering with store and calling its activate routine
        /// </summary>
        /// <param name="grainTypeName"></param>
        /// <param name="data"></param>
        /// <param name="genericArguments"></param>
        /// <returns></returns>
        private void CreateGrainInstance(string grainTypeName, ActivationData data, string genericArguments)
        {
            string grainClassName;
            if (!GrainTypeManager.TryGetPrimaryImplementation(grainTypeName, out grainClassName))
            {
                // Lookup from grain type code
                var typeCode = data.Grain.TypeCode;
                if (typeCode != 0)
                {
                    PlacementStrategy unused;
                    MultiClusterRegistrationStrategy unusedActivationStrategy;
                    GetGrainTypeInfo(typeCode, out grainClassName, out unused, out unusedActivationStrategy, genericArguments);
                }
                else
                {
                    grainClassName = grainTypeName;
                }
            }

            GrainTypeData grainTypeData = GrainTypeManager[grainClassName];

            //Get the grain's type
            Type grainType = grainTypeData.Type;

            lock (data)
            {
                data.SetupContext(grainTypeData, this.serviceProvider);

                Grain grain = grainCreator.CreateGrainInstance(data);
                
                //if grain implements IStreamSubscriptionObserver, then install stream consumer extension on it
                if(grain is IStreamSubscriptionObserver)
                    InstallStreamConsumerExtension(data, grain as IStreamSubscriptionObserver);

                grain.Data = data;
                data.SetGrainInstance(grain);
            }
            
            activations.IncrementGrainCounter(grainClassName);

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("CreateGrainInstance {0}{1}", data.Grain, data.ActivationId);
        }

        private void InstallStreamConsumerExtension(ActivationData result, IStreamSubscriptionObserver observer)
        {
            var invoker = InsideRuntimeClient.TryGetExtensionInvoker(this.GrainTypeManager, typeof(IStreamConsumerExtension));
            if (invoker == null)
                throw new InvalidOperationException("Extension method invoker was not generated for an extension interface");
            var handler = new StreamConsumerExtension(this.providerRuntime, observer);
            result.TryAddExtension(invoker, handler);
        }

        /// <summary>
        /// Try to get runtime data for an activation
        /// </summary>
        /// <param name="activationId"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool TryGetActivationData(ActivationId activationId, out ActivationData data)
        {
            data = null;
            if (activationId.IsSystem) return false;

            data = activations.FindTarget(activationId);
            return data != null;
        }

        private Task DeactivateActivationsFromCollector(List<ActivationData> list)
        {
            logger.Info(ErrorCode.Catalog_ShutdownActivations_1, "DeactivateActivationsFromCollector: total {0} to promptly Destroy.", list.Count);
            CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION).IncrementBy(list.Count);
            foreach (var activation in list)
            {
                lock (activation)
                {
                    activation.PrepareForDeactivation(); // Don't accept any new messages
                }
            }
            return DestroyActivations(list);
        }

        // To be called fro within Activation context.
        // Cannot be awaitable, since after DestroyActivation is done the activation is in Invalid state and cannot await any Task.
        internal void DeactivateActivationOnIdle(ActivationData data)
        {
            DeactivateActivationImpl(data, StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE);
        }

        // To be called fro within Activation context.
        // To be used only if an activation is stuck for a long time, since it can lead to a duplicate activation
        internal void DeactivateStuckActivation(ActivationData activationData)
        {
            DeactivateActivationImpl(activationData, StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_STUCK_ACTIVATION);
            // The unregistration is normally done in the regular deactivation process, but since this activation seems
            // stuck (it might never run the deactivation process), we remove it from the directory directly
            scheduler.RunOrQueueTask(
                () => directory.UnregisterAsync(activationData.Address, UnregistrationCause.Force),
                SchedulingContext)
                .Ignore();
        }

        private void DeactivateActivationImpl(ActivationData data, StatisticName statisticName)
        {
            bool promptly = false;
            bool alreadBeingDestroyed = false;
            lock (data)
            {
                if (data.State == ActivationState.Valid)
                {
                    // Change the ActivationData state here, since we're about to give up the lock.
                    data.PrepareForDeactivation(); // Don't accept any new messages
                    this.activationCollector.TryCancelCollection(data);
                    if (!data.IsCurrentlyExecuting)
                    {
                        promptly = true;
                    }
                    else // busy, so destroy later.
                    {
                        data.AddOnInactive(() => DestroyActivationVoid(data));
                    }
                }
                else if (data.State == ActivationState.Create)
                {
                    throw new InvalidOperationException(String.Format(
                        "Activation {0} has called DeactivateOnIdle from within a constructor, which is not allowed.",
                            data.ToString()));
                }
                else if (data.State == ActivationState.Activating)
                {
                    throw new InvalidOperationException(String.Format(
                        "Activation {0} has called DeactivateOnIdle from within OnActivateAsync, which is not allowed.",
                            data.ToString()));
                }
                else
                {
                    alreadBeingDestroyed = true;
                }
            }
            logger.Info(ErrorCode.Catalog_ShutdownActivations_2,
                "DeactivateActivationOnIdle: {0} {1}.", data.ToString(), promptly ? "promptly" : (alreadBeingDestroyed ? "already being destroyed or invalid" : "later when become idle"));

            CounterStatistic.FindOrCreate(statisticName).Increment();
            if (promptly)
            {
                DestroyActivationVoid(data); // Don't await or Ignore, since we are in this activation context and it may have alraedy been destroyed!
            }
        }

        /// <summary>
        /// Gracefully deletes activations, putting it into a shutdown state to
        /// complete and commit outstanding transactions before deleting it.
        /// To be called not from within Activation context, so can be awaited.
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        internal async Task DeactivateActivations(List<ActivationData> list)
        {
            if (list == null || list.Count == 0) return;

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("DeactivateActivations: {0} activations.", list.Count);
            List<ActivationData> destroyNow = null;
            List<MultiTaskCompletionSource> destroyLater = null;
            int alreadyBeingDestroyed = 0;
            foreach (var d in list)
            {
                var activationData = d; // capture
                lock (activationData)
                {
                    if (activationData.State == ActivationState.Valid)
                    {
                        // Change the ActivationData state here, since we're about to give up the lock.
                        activationData.PrepareForDeactivation(); // Don't accept any new messages
                        this.activationCollector.TryCancelCollection(activationData);
                        if (!activationData.IsCurrentlyExecuting)
                        {
                            if (destroyNow == null)
                            {
                                destroyNow = new List<ActivationData>();
                            }
                            destroyNow.Add(activationData);
                        }
                        else // busy, so destroy later.
                        {
                            if (destroyLater == null)
                            {
                                destroyLater = new List<MultiTaskCompletionSource>();
                            }
                            var tcs = new MultiTaskCompletionSource(1);
                            destroyLater.Add(tcs);
                            activationData.AddOnInactive(() => DestroyActivationAsync(activationData, tcs));
                        }
                    }
                    else
                    {
                        alreadyBeingDestroyed++;
                    }
                }
            }

            int numDestroyNow = destroyNow == null ? 0 : destroyNow.Count;
            int numDestroyLater = destroyLater == null ? 0 : destroyLater.Count;
            logger.Info(ErrorCode.Catalog_ShutdownActivations_3,
                "DeactivateActivations: total {0} to shutdown, out of them {1} promptly, {2} later when become idle and {3} are already being destroyed or invalid.",
                list.Count, numDestroyNow, numDestroyLater, alreadyBeingDestroyed);
            CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DIRECT_SHUTDOWN).IncrementBy(list.Count);

            if (destroyNow != null && destroyNow.Count > 0)
            {
                await DestroyActivations(destroyNow);
            }
            if (destroyLater != null && destroyLater.Count > 0)
            {
                await Task.WhenAll(destroyLater.Select(t => t.Task).ToArray());
            }
        }

        public Task DeactivateAllActivations()
        {
            logger.Info(ErrorCode.Catalog_DeactivateAllActivations, "DeactivateAllActivations.");
            var activationsToShutdown = activations.Where(kv => !kv.Value.IsExemptFromCollection).Select(kv => kv.Value).ToList();
            return DeactivateActivations(activationsToShutdown);
        }

        /// <summary>
        /// Deletes activation immediately regardless of active transactions etc.
        /// For use by grain delete, transaction abort, etc.
        /// </summary>
        /// <param name="activation"></param>
        private void DestroyActivationVoid(ActivationData activation)
        {
            StartDestroyActivations(new List<ActivationData> { activation });
        }

        private void DestroyActivationAsync(ActivationData activation, MultiTaskCompletionSource tcs)
        {
            StartDestroyActivations(new List<ActivationData> { activation }, tcs);
        }

        /// <summary>
        /// Forcibly deletes activations now, without waiting for any outstanding transactions to complete.
        /// Deletes activation immediately regardless of active transactions etc.
        /// For use by grain delete, transaction abort, etc.
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        // Overall code flow:
        // Deactivating state was already set before, in the correct context under lock.
        //      that means no more new requests will be accepted into this activation and all timer were stopped (no new ticks will be delivered or enqueued) 
        // Wait for all already scheduled ticks to finish
        // CallGrainDeactivate
        //      when AsyncDeactivate promise is resolved (NOT when all Deactivate turns are done, which may be orphan tasks):
        // Unregister in the directory 
        //      when all AsyncDeactivate turns are done (Dispatcher.OnActivationCompletedRequest):
        // Set Invalid state
        // UnregisterMessageTarget -> no new tasks will be enqueue (if an orphan task get enqueud, it is ignored and dropped on the floor).
        // InvalidateCacheEntry
        // Reroute pending
        private Task DestroyActivations(List<ActivationData> list)
        {
            var tcs = new MultiTaskCompletionSource(list.Count);
            StartDestroyActivations(list, tcs);
            return tcs.Task;
        }

        private async void StartDestroyActivations(List<ActivationData> list, MultiTaskCompletionSource tcs = null)
        {
            int number = destroyActivationsNumber;
            destroyActivationsNumber++;
            try
            {
                logger.Info(ErrorCode.Catalog_DestroyActivations, "Starting DestroyActivations #{0} of {1} activations", number, list.Count);

                // step 1 - WaitForAllTimersToFinish
                var tasks1 = new List<Task>();
                foreach (var activation in list)
                {
                    tasks1.Add(activation.WaitForAllTimersToFinish());
                }

                try
                {
                    await Task.WhenAll(tasks1);
                }
                catch (Exception exc)
                {
                    logger.Warn(ErrorCode.Catalog_WaitForAllTimersToFinish_Exception, String.Format("WaitForAllTimersToFinish {0} failed.", list.Count), exc);
                }

                // step 2 - CallGrainDeactivate
                var tasks2 = new List<Tuple<Task, ActivationData>>();
                foreach (var activation in list)
                {
                    var activationData = activation; // Capture loop variable
                    var task = scheduler.RunOrQueueTask(() => CallGrainDeactivateAndCleanupStreams(activationData), activationData.SchedulingContext);
                    tasks2.Add(new Tuple<Task, ActivationData>(task, activationData));
                }
                var asyncQueue = new AsyncBatchedContinuationQueue<ActivationData>();
                asyncQueue.Queue(tasks2, tupleList =>
                {
                    FinishDestroyActivations(tupleList.Select(t => t.Item2).ToList(), number, tcs);
                    GC.KeepAlive(asyncQueue); // not sure about GC not collecting the asyncQueue local var prematuraly, so just want to capture it here to make sure. Just to be safe.
                });
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.Catalog_DeactivateActivation_Exception, String.Format("StartDestroyActivations #{0} failed with {1} Activations.", number, list.Count), exc);
            }
        }

        private async void FinishDestroyActivations(List<ActivationData> list, int number, MultiTaskCompletionSource tcs)
        {
            try
            {
                //logger.Info(ErrorCode.Catalog_DestroyActivations_Done, "Starting FinishDestroyActivations #{0} - with {1} Activations.", number, list.Count);
                // step 3 - UnregisterManyAsync
                try
                {            
                    List<ActivationAddress> activationsToDeactivate = list.
                        Where((ActivationData d) => d.IsUsingGrainDirectory).
                        Select((ActivationData d) => ActivationAddress.GetAddress(LocalSilo, d.Grain, d.ActivationId)).ToList();

                    if (activationsToDeactivate.Count > 0)
                    {
                        await scheduler.RunOrQueueTask(() =>
                            directory.UnregisterManyAsync(activationsToDeactivate, UnregistrationCause.Force),
                            SchedulingContext);
                    }
                }
                catch (Exception exc)
                {
                    logger.Warn(ErrorCode.Catalog_UnregisterManyAsync, String.Format("UnregisterManyAsync {0} failed.", list.Count), exc);
                }

                // step 4 - UnregisterMessageTarget and OnFinishedGrainDeactivate
                foreach (var activationData in list)
                {
                    Grain grainInstance = activationData.GrainInstance;
                    try
                    {
                        lock (activationData)
                        {
                            activationData.SetState(ActivationState.Invalid); // Deactivate calls on this activation are finished
                        }
                        UnregisterMessageTarget(activationData);
                    }
                    catch (Exception exc)
                    {
                        logger.Warn(ErrorCode.Catalog_UnregisterMessageTarget2, String.Format("UnregisterMessageTarget failed on {0}.", activationData), exc);
                    }

                    // IMPORTANT: no more awaits and .Ignore after that point.

                    // Just use this opportunity to invalidate local Cache Entry as well. 
                    // If this silo is not the grain directory partition for this grain, it may have it in its cache.
                    try
                    {

                        directory.InvalidateCacheEntry(activationData.Address);

                        RerouteAllQueuedMessages(activationData, null, "Finished Destroy Activation");
                    }
                    catch (Exception exc)
                    {
                        logger.Warn(ErrorCode.Catalog_UnregisterMessageTarget3, String.Format("Last stage of DestroyActivations failed on {0}.", activationData), exc);
                    }
                    try
                    {
                        if (grainInstance != null)
                        {
                            lock (activationData)
                            {
                                grainCreator.Release(activationData, grainInstance);
                            }
                        }

                        activationData.Dispose();
                    }
                    catch (Exception exc)
                    {
                        logger.Warn(ErrorCode.Catalog_UnregisterMessageTarget3, String.Format("Releasing of the grain instance and scope failed on {0}.", activationData), exc);
                    }
                }
                // step 5 - Resolve any waiting TaskCompletionSource
                if (tcs != null)
                {
                    tcs.SetMultipleResults(list.Count);
                }
                logger.Info(ErrorCode.Catalog_DestroyActivations_Done, "Done FinishDestroyActivations #{0} - Destroyed {1} Activations.", number, list.Count);
            }catch (Exception exc)
            {
                logger.Error(ErrorCode.Catalog_FinishDeactivateActivation_Exception, String.Format("FinishDestroyActivations #{0} failed with {1} Activations.", number, list.Count), exc);
            }
        }
        private void RerouteAllQueuedMessages(ActivationData activation, ActivationAddress forwardingAddress, string failedOperation, Exception exc = null)
        {
            lock (activation)
            {
                List<Message> msgs = activation.DequeueAllWaitingMessages();
                if (msgs == null || msgs.Count <= 0) return;

                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_RerouteAllQueuedMessages, String.Format("RerouteAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count(), activation));
                this.Dispatcher.ProcessRequestsToInvalidActivation(msgs, activation.Address, forwardingAddress, failedOperation, exc);
            }
        }

        /// <summary>
        /// Rejects all messages enqueued for the provided activation.
        /// </summary>
        /// <param name="activation">The activation.</param>
        /// <param name="failedOperation">The operation which failed, resulting in this rejection.</param>
        /// <param name="exception">The rejection exception.</param>
        private void RejectAllQueuedMessages(
            ActivationData activation,
            string failedOperation,
            Exception exception = null)
        {
            lock (activation)
            {
                List<Message> msgs = activation.DequeueAllWaitingMessages();
                if (msgs == null || msgs.Count <= 0) return;

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug(
                        ErrorCode.Catalog_RerouteAllQueuedMessages,
                        string.Format("RejectAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count(), activation));
                this.Dispatcher.ProcessRequestsToInvalidActivation(
                    msgs,
                    activation.Address,
                    forwardingAddress: null,
                    failedOperation: failedOperation,
                    exc: exception,
                    rejectMessages: true);
            }
        }

        private async Task CallGrainActivate(ActivationData activation, Dictionary<string, object> requestContextData)
        {
            var grainTypeName = activation.GrainInstanceType.FullName;

            // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_BeforeCallingActivate, "About to call {1} grain's OnActivateAsync() method {0}", activation, grainTypeName);

            // Start grain lifecycle within try-catch wrapper to safely capture any exceptions thrown from called function
            try
            {
                RequestContextExtensions.Import(requestContextData);
                await activation.Lifecycle.OnStart();
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_AfterCallingActivate, "Returned from calling {1} grain's OnActivateAsync() method {0}", activation, grainTypeName);

                lock (activation)
                {
                    if (activation.State == ActivationState.Activating)
                    {
                        activation.SetState(ActivationState.Valid); // Activate calls on this activation are finished
                    }
                    if (!activation.IsCurrentlyExecuting)
                    {
                        activation.RunOnInactive();
                    }
                    // Run message pump to see if there is a new request is queued to be processed
                    this.Dispatcher.RunMessagePump(activation);
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Catalog_ErrorCallingActivate,
                    string.Format("Error calling grain's OnActivateAsync() method - Grain type = {1} Activation = {0}", activation, grainTypeName), exc);

                activation.SetState(ActivationState.Invalid); // Mark this activation as unusable

                activationsFailedToActivate.Increment();

                // TODO: During lifecycle refactor discuss with team whether activation failure should have a well defined exception, or throw whatever
                //   exception caused activation to fail, with no indication that it occured durring activation
                //   rather than the grain call.
                OrleansLifecycleCanceledException canceledException = exc as OrleansLifecycleCanceledException;
                if(canceledException?.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(canceledException.InnerException).Throw();
                }
                throw;
            }
        }

        private async Task<ActivationData> CallGrainDeactivateAndCleanupStreams(ActivationData activation)
        {
            try
            {
                var grainTypeName = activation.GrainInstanceType.FullName;

                // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_BeforeCallingDeactivate, "About to call {1} grain's OnDeactivateAsync() method {0}", activation, grainTypeName);

                // Call OnDeactivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
                try
                {
                    // just check in case this activation data is already Invalid or not here at all.
                    ActivationData ignore;
                    if (TryGetActivationData(activation.ActivationId, out ignore) &&
                        activation.State == ActivationState.Deactivating)
                    {
                        RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake. 
                        await activation.Lifecycle.OnStop();
                    }
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_AfterCallingDeactivate, "Returned from calling {1} grain's OnDeactivateAsync() method {0}", activation, grainTypeName);
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.Catalog_ErrorCallingDeactivate,
                        string.Format("Error calling grain's OnDeactivateAsync() method - Grain type = {1} Activation = {0}", activation, grainTypeName), exc);
                }

                if (activation.IsUsingStreams)
                {
                    try
                    {
                        await activation.DeactivateStreamResources();
                    }
                    catch (Exception exc)
                    {
                        logger.Warn(ErrorCode.Catalog_DeactivateStreamResources_Exception, String.Format("DeactivateStreamResources Grain type = {0} Activation = {1} failed.", grainTypeName, activation), exc);
                    }
                }

                if (activation.GrainInstance is ILogConsistencyProtocolParticipant)
                {
                    await ((ILogConsistencyProtocolParticipant)activation.GrainInstance).DeactivateProtocolParticipant();
                }
            }
            catch(Exception exc)
            {
                logger.Error(ErrorCode.Catalog_FinishGrainDeactivateAndCleanupStreams_Exception, String.Format("CallGrainDeactivateAndCleanupStreams Activation = {0} failed.", activation), exc);
            }
            return activation;
        }

        /// <summary>
        /// Represents the results of an attempt to register an activation.
        /// </summary>
        private struct ActivationRegistrationResult
        {
            /// <summary>
            /// Represents a successful activation.
            /// </summary>
            public static readonly ActivationRegistrationResult Success = new ActivationRegistrationResult
            {
                IsSuccess = true       
            };

            public ActivationRegistrationResult(ActivationAddress existingActivationAddress)
            {
                ValidateExistingActivationAddress(existingActivationAddress);
                ExistingActivationAddress = existingActivationAddress;
                IsSuccess = false;
            }
            
            /// <summary>
            /// Returns true if this instance represents a successful registration, false otheriwse.
            /// </summary>
            public bool IsSuccess { get; private set; }

            /// <summary>
            /// The existing activation address if this instance represents a duplicate activation.
            /// </summary>
            public ActivationAddress ExistingActivationAddress { get; }

            private static void ValidateExistingActivationAddress(ActivationAddress existingActivationAddress)
            {
                if (existingActivationAddress == null)
                    throw new ArgumentNullException(nameof(existingActivationAddress));
            }
        }

        private async Task<ActivationRegistrationResult> RegisterActivationInGrainDirectoryAndValidate(ActivationData activation)
        {
            ActivationAddress address = activation.Address;
            // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker. 
            // Among those that are registered in the directory, we currently do not have any multi activations.
            if (activation.IsUsingGrainDirectory)
            {
                var result = await scheduler.RunOrQueueTask(() => directory.RegisterAsync(address, singleActivation:true), this.SchedulingContext);
                if (address.Equals(result.Address)) return ActivationRegistrationResult.Success;
               
                return new ActivationRegistrationResult(existingActivationAddress: result.Address);
            }
            else
            {
                StatelessWorkerPlacement stPlacement = activation.PlacedUsing as StatelessWorkerPlacement;
                int maxNumLocalActivations = stPlacement.MaxLocal;
                lock (activations)
                {
                    List<ActivationData> local;
                    if (!LocalLookup(address.Grain, out local) || local.Count <= maxNumLocalActivations)
                        return ActivationRegistrationResult.Success;

                    var id = StatelessWorkerDirector.PickRandom(local).Address;
                    return new ActivationRegistrationResult(existingActivationAddress: id);
                }
            }
            // We currently don't have any other case for multiple activations except for StatelessWorker. 
        }

#endregion
#region Activations - private

        /// <summary>
        /// Invoke the activate method on a newly created activation
        /// </summary>
        /// <param name="activation"></param>
        /// <param name="requestContextData"></param>
        /// <returns></returns>
        private Task InvokeActivate(ActivationData activation, Dictionary<string, object> requestContextData)
        {
            // NOTE: This should only be called with the correct schedulering context for the activation to be invoked.
            lock (activation)
            {
                activation.SetState(ActivationState.Activating);
            }
            return scheduler.QueueTask(() => CallGrainActivate(activation, requestContextData), activation.SchedulingContext); // Target grain's scheduler context);
            // ActivationData will transition out of ActivationState.Activating via Dispatcher.OnActivationCompletedRequest
        }
#endregion
#region IPlacementRuntime

        public bool FastLookup(GrainId grain, out AddressesAndTag addresses)
        {
            return directory.LocalLookup(grain, out addresses) && addresses.Addresses != null && addresses.Addresses.Count > 0;
            // NOTE: only check with the local directory cache.
            // DO NOT check in the local activations TargetDirectory!!!
            // The only source of truth about which activation should be legit to is the state of the ditributed directory.
            // Everyone should converge to that (that is the meaning of "eventualy consistency - eventualy we converge to one truth").
            // If we keep using the local activation, it may not be registered in th directory any more, but we will never know that and keep using it,
            // thus volaiting the single-activation semantics and not converging even eventualy!
        }

        public Task<AddressesAndTag> FullLookup(GrainId grain)
        {
            return scheduler.RunOrQueueTask(() => directory.LookupAsync(grain), this.SchedulingContext);
        }

        public Task<AddressesAndTag> LookupInCluster(GrainId grain, string clusterId)
        {
            return scheduler.RunOrQueueTask(() => directory.LookupInCluster(grain, clusterId), this.SchedulingContext);
        }


        public bool LocalLookup(GrainId grain, out List<ActivationData> addresses)
        {
            addresses = activations.FindTargets(grain);
            return addresses != null;
        }

        public List<SiloAddress> AllActiveSilos
        {
            get
            {
                var result = SiloStatusOracle.GetApproximateSiloStatuses(true).Select(s => s.Key).ToList();
                if (result.Count > 0) return result;

                logger.Warn(ErrorCode.Catalog_GetApproximateSiloStatuses, "AllActiveSilos SiloStatusOracle.GetApproximateSiloStatuses empty");
                return new List<SiloAddress> { LocalSilo };
            }
        }

        public SiloStatus LocalSiloStatus
        {
            get {
                return SiloStatusOracle.CurrentStatus;
            }
        }

#endregion
#region Implementation of ICatalog

        public Task DeleteActivations(List<ActivationAddress> addresses)
        {
            return DestroyActivations(TryGetActivationDatas(addresses));
        }


        private List<ActivationData> TryGetActivationDatas(List<ActivationAddress> addresses)
        {
            var datas = new List<ActivationData>(addresses.Count);
            foreach (var activationAddress in addresses)
            {
                ActivationData data;
                if (TryGetActivationData(activationAddress.Activation, out data))
                    datas.Add(data);
            }
            return datas;
        }

#endregion

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

            var activationsToShutdown = new List<ActivationData>();
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
                            if (!activationData.IsUsingGrainDirectory) continue;
                            if (!directory.GetPrimaryForGrain(activationData.Grain).Equals(updatedSilo)) continue;

                            lock (activationData)
                            {
                                // adapted from InsideGrainClient.DeactivateOnIdle().
                                activationData.ResetKeepAliveRequest();
                                activationsToShutdown.Add(activationData);
                            }
                        }
                        catch (Exception exc)
                        {
                            logger.Error(ErrorCode.Catalog_SiloStatusChangeNotification_Exception,
                                String.Format("Catalog has thrown an exception while executing OnSiloStatusChange of silo {0}.", updatedSilo.ToStringWithHashCode()), exc);
                        }
                    }
                }
                logger.Info(ErrorCode.Catalog_SiloStatusChangeNotification,
                    String.Format("Catalog is deactivating {0} activations due to a failure of silo {1}, since it is a primary directory partition to these grain ids.",
                        activationsToShutdown.Count, updatedSilo.ToStringWithHashCode()));
            }
            finally
            {
                // outside the lock.
                if (activationsToShutdown.Count > 0)
                {
                    DeactivateActivations(activationsToShutdown).Ignore();
                }
            }
        }
    }
}
