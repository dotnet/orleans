using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.GrainDirectory;
using Orleans.MultiCluster;
using Orleans.Providers;
using Orleans.LogConsistency;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Runtime
{
    internal class Catalog : SystemTarget, ICatalog, IPlacementContext, ISiloStatusListener
    {
        /// <summary>
        /// Exception to indicate that the activation would have been a duplicate so messages pending for it should be redirected.
        /// </summary>
        [Serializable]
        internal class DuplicateActivationException : Exception
        {
            public ActivationAddress ActivationToUse { get; private set; }

            public SiloAddress PrimaryDirectoryForGrain { get; private set; } // for diagnostics only!

            public DuplicateActivationException() : base("DuplicateActivationException") { }
            public DuplicateActivationException(string msg) : base(msg) { }
            public DuplicateActivationException(string message, Exception innerException) : base(message, innerException) { }

            public DuplicateActivationException(ActivationAddress activationToUse)
                : base("DuplicateActivationException")
            {
                ActivationToUse = activationToUse;
            }

            public DuplicateActivationException(ActivationAddress activationToUse, SiloAddress primaryDirectoryForGrain)
                : base("DuplicateActivationException")
            {
                ActivationToUse = activationToUse;
                PrimaryDirectoryForGrain = primaryDirectoryForGrain;
            }

#if !NETSTANDARD
            // Implementation of exception serialization with custom properties according to:
            // http://stackoverflow.com/questions/94488/what-is-the-correct-way-to-make-a-custom-net-exception-serializable
            protected DuplicateActivationException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
                if (info != null)
                {
                    ActivationToUse = (ActivationAddress) info.GetValue("ActivationToUse", typeof (ActivationAddress));
                    PrimaryDirectoryForGrain = (SiloAddress) info.GetValue("PrimaryDirectoryForGrain", typeof (SiloAddress));
                }
            }

            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                if (info != null)
                {
                    info.AddValue("ActivationToUse", ActivationToUse, typeof (ActivationAddress));
                    info.AddValue("PrimaryDirectoryForGrain", PrimaryDirectoryForGrain, typeof (SiloAddress));                   
                }
                // MUST call through to the base class to let it save its own state
                base.GetObjectData(info, context);
            }
#endif
        }

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

#if !NETSTANDARD
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
#endif
        }


        public GrainTypeManager GrainTypeManager { get; private set; }

        public SiloAddress LocalSilo { get; private set; }
        internal ISiloStatusOracle SiloStatusOracle { get; set; }
        internal readonly ActivationCollector ActivationCollector;

        private readonly ILocalGrainDirectory directory;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ActivationDirectory activations;
        private IStorageProviderManager storageProviderManager;
        private ILogConsistencyProviderManager logConsistencyProviderManager;
        private readonly Logger logger;
        private int collectionNumber;
        private int destroyActivationsNumber;
        private IDisposable gcTimer;
        private readonly GlobalConfiguration config;
        private readonly string localSiloName;
        private readonly CounterStatistic activationsCreated;
        private readonly CounterStatistic activationsDestroyed;
        private readonly CounterStatistic activationsFailedToActivate;
        private readonly IntValueStatistic inProcessRequests;
        private readonly CounterStatistic collectionCounter;
        private readonly GrainCreator grainCreator;
        private readonly NodeConfiguration nodeConfig;
        private readonly TimeSpan maxRequestProcessingTime;
        private readonly TimeSpan maxWarningRequestProcessingTime;
        private readonly SerializationManager serializationManager;

        private readonly MultiClusterRegistrationStrategyManager multiClusterRegistrationStrategyManager;

        public Catalog(
            ILocalSiloDetails localSiloDetails,
            ILocalGrainDirectory grainDirectory,
            GrainTypeManager typeManager,
            OrleansTaskScheduler scheduler,
            ActivationDirectory activationDirectory,
            ClusterConfiguration config,
            GrainCreator grainCreator,
            NodeConfiguration nodeConfig,
            ISiloMessageCenter messageCenter,
            PlacementDirectorsManager placementDirectorsManager,
            MessageFactory messageFactory,
            SerializationManager serializationManager,
            MultiClusterRegistrationStrategyManager multiClusterRegistrationStrategyManager)
            : base(Constants.CatalogId, messageCenter.MyAddress)
        {
            LocalSilo = localSiloDetails.SiloAddress;
            localSiloName = localSiloDetails.Name;
            directory = grainDirectory;
            activations = activationDirectory;
            this.scheduler = scheduler;
            GrainTypeManager = typeManager;
            collectionNumber = 0;
            destroyActivationsNumber = 0;
            this.grainCreator = grainCreator;
            this.nodeConfig = nodeConfig;
            this.serializationManager = serializationManager;
            this.multiClusterRegistrationStrategyManager = multiClusterRegistrationStrategyManager;

            logger = LogManager.GetLogger("Catalog", Runtime.LoggerType.Runtime);
            this.config = config.Globals;
            ActivationCollector = new ActivationCollector(config);
            this.Dispatcher = new Dispatcher(scheduler, messageCenter, this, config, placementDirectorsManager, grainDirectory, messageFactory);
            GC.GetTotalMemory(true); // need to call once w/true to ensure false returns OK value

            config.OnConfigChange("Globals/Activation", () => scheduler.RunOrQueueAction(Start, SchedulingContext), false);
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
            maxWarningRequestProcessingTime = this.config.ResponseTimeout.Multiply(5);
            maxRequestProcessingTime = this.config.MaxRequestProcessingTime;
        }

        /// <summary>
        /// Gets the dispatcher used by this instance.
        /// </summary>
        public Dispatcher Dispatcher { get; }

        public IList<SiloAddress> GetCompatibleSiloList(GrainId grain)
        {
            // For test only: if we have silos that are not yet in the Cluster TypeMap, we assume that they are compatible
            // with the current silo
            if (this.config.AssumeHomogenousSilosForTesting)
                return AllActiveSilos;

            var typeCode = grain.GetTypeCode();
            var compatibleSilos = GrainTypeManager.GetSupportedSilos(typeCode).Intersect(AllActiveSilos).ToList();
            if (compatibleSilos.Count == 0)
                throw new OrleansException($"TypeCode ${typeCode} not supported in the cluster");

            return compatibleSilos;
        }

        internal void SetStorageManager(IStorageProviderManager storageManager)
        {
            storageProviderManager = storageManager;
        }

        internal void SetLogConsistencyManager(ILogConsistencyProviderManager logConsistencyManager)
        {
            logConsistencyProviderManager = logConsistencyManager;
        } 

        internal void Start()
        {
            if (gcTimer != null) gcTimer.Dispose();

            var t = GrainTimer.FromTaskCallback(
                this.RuntimeClient.Scheduler,
                OnTimer,
                null,
                TimeSpan.Zero,
                ActivationCollector.Quantum,
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
                number, memBefore, activations.Count, ActivationCollector.ToString());
            List<ActivationData> list = scanStale ? ActivationCollector.ScanStale() : ActivationCollector.ScanAll(ageLimit);
            collectionCounter.Increment();
            var count = 0;
            if (list != null && list.Count > 0)
            {
                count = list.Count;
                if (logger.IsVerbose) logger.Verbose("CollectActivations{0}", list.ToStrings(d => d.Grain.ToString() + d.ActivationId));
                await DeactivateActivationsFromCollector(list);
            }
            long memAfter = GC.GetTotalMemory(false) / (1024 * 1024);
            watch.Stop();
            logger.Info(ErrorCode.Catalog_AfterCollection, "After collection#{0}: memory={1}MB, #activations={2}, collected {3} activations, collector={4}, collection time={5}.",
                number, memAfter, activations.Count, count, ActivationCollector.ToString(), watch.Elapsed);
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
                GrainTypeManager.GetTypeInfo(grain.GetTypeCode(), out grainClassName, out unused, out unusedActivationStrategy);
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
            ActivationCollector.TryCancelCollection(activation);
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
            activatedPromise = TaskDone.Done;
            PlacementStrategy placement;

            lock (activations)
            {
                if (TryGetActivationData(address.Activation, out result))
                {
                    return result;
                }
                
                int typeCode = address.Grain.GetTypeCode();
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
                    // create a dummy activation that will queue up messages until the real data arrives
                    // We want to do this (RegisterMessageTarget) under the same lock that we tested TryGetActivationData. They both access ActivationDirectory.
                    result = new ActivationData(
                        address, 
                        genericArguments, 
                        placement, 
                        activationStrategy,
                        ActivationCollector, 
                        config.Application.GetCollectionAgeLimit(grainType),
                        this.nodeConfig,
                        this.maxWarningRequestProcessingTime,
                        this.maxRequestProcessingTime,
                        this.RuntimeClient);
                    RegisterMessageTarget(result);
                }
            } // End lock

            // Did not find and did not start placing new
            if (result == null)
            {
                var msg = String.Format("Non-existent activation: {0}, grain type: {1}.",
                                           address.ToFullString(), grainType);
                if (logger.IsVerbose) logger.Verbose(ErrorCode.CatalogNonExistingActivation2, msg);
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

        private async Task InitActivation(ActivationData activation, string grainType, string genericArguments, Dictionary<string, object> requestContextData)
        {
            // We've created a dummy activation, which we'll eventually return, but in the meantime we'll queue up (or perform promptly)
            // the operations required to turn the "dummy" activation into a real activation
            ActivationAddress address = activation.Address;

            int initStage = 0;
            // A chain of promises that will have to complete in order to complete the activation
            // Register with the grain directory, register with the store if necessary and call the Activate method on the new activation.
            try
            {
                initStage = 1;
                await RegisterActivationInGrainDirectoryAndValidate(activation);

                initStage = 2;
                await SetupActivationState(activation, String.IsNullOrEmpty(genericArguments) ? grainType : $"{grainType}[{genericArguments}]");

                initStage = 3;
                await InvokeActivate(activation, requestContextData);

                ActivationCollector.ScheduleCollection(activation);

                // Success!! Log the result, and start processing messages
                if (logger.IsVerbose) logger.Verbose("InitActivation is done: {0}", address);
            }
            catch (Exception ex)
            {
                lock (activation)
                {
                    activation.SetState(ActivationState.Invalid);
                    try
                    {
                        UnregisterMessageTarget(activation);
                    }
                    catch (Exception exc)
                    {
                        logger.Warn(ErrorCode.Catalog_UnregisterMessageTarget4, String.Format("UnregisterMessageTarget failed on {0}.", activation), exc);
                    }

                    switch (initStage)
                    {
                        case 1: // failed to RegisterActivationInGrainDirectory
                            
                            ActivationAddress target = null;
                            Exception dupExc;
                            // Failure!! Could it be that this grain uses single activation placement, and there already was an activation?
                            if (Utils.TryFindException(ex, typeof (DuplicateActivationException), out dupExc))
                            {
                                target = ((DuplicateActivationException) dupExc).ActivationToUse;
                                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_DUPLICATE_ACTIVATIONS)
                                    .Increment();
                            }
                            activation.ForwardingAddress = target;
                            if (target != null)
                            {
                                var primary = ((DuplicateActivationException)dupExc).PrimaryDirectoryForGrain;
                                // If this was a duplicate, it's not an error, just a race.
                                // Forward on all of the pending messages, and then forget about this activation.
                                string logMsg = String.Format("Tried to create a duplicate activation {0}, but we'll use {1} instead. " +
                                    "GrainInstanceType is {2}. " +
                                                            "{3}" +
                                                            "Full activation address is {4}. We have {5} messages to forward.",
                                    address,
                                    target,
                                    activation.GrainInstanceType,
                                                primary != null ? "Primary Directory partition for this grain is " + primary + ". " : String.Empty,
                                    address.ToFullString(),
                                    activation.WaitingCount);
                                if (activation.IsUsingGrainDirectory)
                                {
                                    logger.Info(ErrorCode.Catalog_DuplicateActivation, logMsg);
                                }
                                else
                                {
                                    if (logger.IsVerbose) logger.Verbose(ErrorCode.Catalog_DuplicateActivation, logMsg);
                                }
                                RerouteAllQueuedMessages(activation, target, "Duplicate activation", ex);
                            }
                            else
                            {
                                logger.Warn(ErrorCode.Runtime_Error_100064,
                                    String.Format("Failed to RegisterActivationInGrainDirectory for {0}.",
                                        activation), ex);
                                // Need to undo the registration we just did earlier
                                if (activation.IsUsingGrainDirectory)
                                {
                                    scheduler.RunOrQueueTask(() => directory.UnregisterAsync(address, UnregistrationCause.Force),
                                        SchedulingContext).Ignore();
                                }
                                RerouteAllQueuedMessages(activation, null,
                                    "Failed RegisterActivationInGrainDirectory", ex);
                            }
                            break;

                        case 2: // failed to setup persistent state
                            
                            logger.Warn(ErrorCode.Catalog_Failed_SetupActivationState,
                                String.Format("Failed to SetupActivationState for {0}.", activation), ex);
                            // Need to undo the registration we just did earlier
                            if (activation.IsUsingGrainDirectory)
                            {
                                scheduler.RunOrQueueTask(() => directory.UnregisterAsync(address, UnregistrationCause.Force),
                                    SchedulingContext).Ignore();
                            }

                            RerouteAllQueuedMessages(activation, null, "Failed SetupActivationState", ex);
                            break;

                        case 3: // failed to InvokeActivate
                            
                            logger.Warn(ErrorCode.Catalog_Failed_InvokeActivate,
                                String.Format("Failed to InvokeActivate for {0}.", activation), ex);
                            // Need to undo the registration we just did earlier
                            if (activation.IsUsingGrainDirectory)
                            {
                                scheduler.RunOrQueueTask(() => directory.UnregisterAsync(address, UnregistrationCause.Force),
                                    SchedulingContext).Ignore();
                            }

                            RerouteAllQueuedMessages(activation, null, "Failed InvokeActivate", ex);
                            break;
                    }
                }
                throw;
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
                var typeCode = data.Grain.GetTypeCode();
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

            //Gets the type for the grain's state
            Type stateObjectType = grainTypeData.StateObjectType;
            
            lock (data)
            {
                Grain grain;

                //Create a new instance of the given grain type
                grain = grainCreator.CreateGrainInstance(grainType, data.Identity);

                //for stateful grains, install storage bridge
                if (grain is IStatefulGrain)
                {
                    SetupStorageProvider(grainType, data);

                    var storage = new GrainStateStorageBridge(grainType.FullName, data.StorageProvider);

                    grain = grainCreator.CreateGrainInstance(grainType, data.Identity, stateObjectType, storage);

                    storage.SetGrain(grain);
                }

                //for log-view grains, install log-view adaptor
                else if (grain is ILogConsistentGrain)
                {
                    var consistencyProvider = SetupLogConsistencyProvider(grain, grainType, data);                  
                    grainCreator.InstallLogViewAdaptor(grain, grainType, 
                        grainTypeData.StateObjectType, grainTypeData.MultiClusterRegistrationStrategy ?? this.multiClusterRegistrationStrategyManager.DefaultStrategy,
                        consistencyProvider, data.StorageProvider);
                }
             
                grain.Data = data;
                data.SetGrainInstance(grain);
            }


            activations.IncrementGrainCounter(grainClassName);

            if (logger.IsVerbose) logger.Verbose("CreateGrainInstance {0}{1}", data.Grain, data.ActivationId);
        }

        private void SetupStorageProvider(Type grainType, ActivationData data)
        {
            var grainTypeName = grainType.FullName;

            // Get the storage provider name, using the default if not specified.
            var attr = grainType.GetTypeInfo().GetCustomAttributes<StorageProviderAttribute>(true).FirstOrDefault();
            var storageProviderName = attr != null ? attr.ProviderName : Constants.DEFAULT_STORAGE_PROVIDER_NAME;

            IStorageProvider provider;
            if (storageProviderManager == null || storageProviderManager.GetNumLoadedProviders() == 0)
            {
                var errMsg = string.Format("No storage providers found loading grain type {0}", grainTypeName);
                logger.Error(ErrorCode.Provider_CatalogNoStorageProvider_1, errMsg);
                throw new BadProviderConfigException(errMsg);
            }
            if (string.IsNullOrWhiteSpace(storageProviderName))
            {
                // Use default storage provider
                provider = storageProviderManager.GetDefaultProvider();
            }
            else
            {
                // Look for MemoryStore provider as special case name
                bool caseInsensitive = Constants.MEMORY_STORAGE_PROVIDER_NAME.Equals(storageProviderName, StringComparison.OrdinalIgnoreCase);
                storageProviderManager.TryGetProvider(storageProviderName, out provider, caseInsensitive);
                if (provider == null)
                {
                    var errMsg = string.Format(
                        "Cannot find storage provider with Name={0} for grain type {1}", storageProviderName,
                        grainTypeName);
                    logger.Error(ErrorCode.Provider_CatalogNoStorageProvider_2, errMsg);
                    throw new BadProviderConfigException(errMsg);
                }
            }
            data.StorageProvider = provider;

            if (logger.IsVerbose2)
            {
                string msg = string.Format("Assigned storage provider with Name={0} to grain type {1}",
                    storageProviderName, grainTypeName);
                logger.Verbose2(ErrorCode.Provider_CatalogStorageProviderAllocated, msg);
            }
        }

        private ILogViewAdaptorFactory SetupLogConsistencyProvider(Grain grain, Type grainType, ActivationData data)
        {
            var attr = grainType.GetTypeInfo().GetCustomAttributes<LogConsistencyProviderAttribute>(true).FirstOrDefault();
            var consistencyProviderName = attr?.ProviderName;

            ILogConsistencyProvider consistencyProvider;

            if (logConsistencyProviderManager == null)
            {
                var errMsg = string.Format("No consistency provider manager found loading grain type {0}", grainType.FullName);
                logger.Error(ErrorCode.Provider_CatalogNoLogConsistencyProvider, errMsg);
                throw new BadProviderConfigException(errMsg);
            }

            if (!string.IsNullOrWhiteSpace(consistencyProviderName))
            {
                // find the named consistency provider; throw exception if it is not in the config
                if (!logConsistencyProviderManager.TryGetProvider(consistencyProviderName, out consistencyProvider, false))
                {
                    var errMsg = string.Format(
                        "Cannot find consistency provider with Name={0} for grain type {1}", attr.ProviderName,
                        grainType.FullName);
                    logger.Error(ErrorCode.Provider_CatalogNoLogConsistencyProvider, errMsg);
                    throw new BadProviderConfigException(errMsg);
                }
            }
            else
            {
                // See if the config specifies a "Default" consistency provider; if so use that
                logConsistencyProviderManager.TryGetProvider(Constants.DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME, out consistencyProvider, true);
            }

            if (consistencyProvider != null)
            {
                // we found a log consistency provider in the configuration file

                // if it depends on a storage provider, find that one too
                if (consistencyProvider.UsesStorageProvider)
                    SetupStorageProvider(grainType, data);

                string msg = string.Format("Assigned log consistency provider with Name={0} to grain type {1}",
                    attr.ProviderName, grainType.FullName);
                logger.Verbose2(ErrorCode.Provider_CatalogLogConsistencyProviderAllocated, msg);

                return consistencyProvider;
            }

            // Case 2 : no log consistency provider was specified in the configuration file. 
            // now we check if the grain type specifies a default adaptor factory

            var defaultFactory = ((ILogConsistentGrain)grain).DefaultAdaptorFactory;

            if (defaultFactory == null)
            {
                var errMsg = string.Format("No log consistency provider found loading grain type {0}", grainType.FullName);
                logger.Error(ErrorCode.Provider_CatalogNoLogConsistencyProvider, errMsg);
                throw new BadProviderConfigException(errMsg);
            };

            // if it depends on a storage provider, find that one too
            if (defaultFactory.UsesStorageProvider)
                SetupStorageProvider(grainType, data);

            return defaultFactory;
        }
       

        private async Task SetupActivationState(ActivationData result, string grainType)
        {
            var statefulGrain = result.GrainInstance as IStatefulGrain;
            if (statefulGrain == null)
            {
                return;
            }

            var state = statefulGrain.GrainState;

            if (result.StorageProvider != null && state != null)
            {
                var sw = Stopwatch.StartNew();
                var innerState = statefulGrain.GrainState.State;

                // Populate state data
                try
                {
                    var grainRef = result.GrainReference;

                    await scheduler.RunOrQueueTask(() =>
                        result.StorageProvider.ReadStateAsync(grainType, grainRef, state),
                        result.SchedulingContext);
                    
                    sw.Stop();
                    StorageStatisticsGroup.OnStorageActivate(result.StorageProvider, grainType, result.GrainReference, sw.Elapsed);
                }
                catch (Exception ex)
                {
                    StorageStatisticsGroup.OnStorageActivateError(result.StorageProvider, grainType, result.GrainReference);
                    sw.Stop();
                    if (!(ex.GetBaseException() is KeyNotFoundException))
                        throw;

                    statefulGrain.GrainState.State = innerState; // Just keep original empty state object
                }
            }
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
                    ActivationCollector.TryCancelCollection(data);
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

            if (logger.IsVerbose) logger.Verbose("DeactivateActivations: {0} activations.", list.Count);
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
                        ActivationCollector.TryCancelCollection(activationData);
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
                    try
                    {
                        // IMPORTANT: no more awaits and .Ignore after that point.

                        // Just use this opportunity to invalidate local Cache Entry as well. 
                        // If this silo is not the grain directory partition for this grain, it may have it in its cache.
                        directory.InvalidateCacheEntry(activationData.Address);

                        RerouteAllQueuedMessages(activationData, null, "Finished Destroy Activation");
                    }
                    catch (Exception exc)
                    {
                        logger.Warn(ErrorCode.Catalog_UnregisterMessageTarget3, String.Format("Last stage of DestroyActivations failed on {0}.", activationData), exc);
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

                if (logger.IsVerbose) logger.Verbose(ErrorCode.Catalog_RerouteAllQueuedMessages, String.Format("RerouteAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count(), activation));
                this.Dispatcher.ProcessRequestsToInvalidActivation(msgs, activation.Address, forwardingAddress, failedOperation, exc);
            }
        }

        private async Task CallGrainActivate(ActivationData activation, Dictionary<string, object> requestContextData)
        {
            var grainTypeName = activation.GrainInstanceType.FullName;

            if (activation.GrainInstance is ILogConsistencyProtocolParticipant)
            {
                await ((ILogConsistencyProtocolParticipant)activation.GrainInstance).PreActivateProtocolParticipant();
            }

            // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
            if (logger.IsVerbose) logger.Verbose(ErrorCode.Catalog_BeforeCallingActivate, "About to call {1} grain's OnActivateAsync() method {0}", activation, grainTypeName);

            // Call OnActivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
            try
            {
                RequestContext.Import(requestContextData);
                await activation.GrainInstance.OnActivateAsync();

                if (logger.IsVerbose) logger.Verbose(ErrorCode.Catalog_AfterCallingActivate, "Returned from calling {1} grain's OnActivateAsync() method {0}", activation, grainTypeName);

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

                throw;
            }

            if (activation.GrainInstance is ILogConsistencyProtocolParticipant)
            {
                await ((ILogConsistencyProtocolParticipant)activation.GrainInstance).PostActivateProtocolParticipant();
            }
        }

        private async Task<ActivationData> CallGrainDeactivateAndCleanupStreams(ActivationData activation)
        {
            try
            {
                var grainTypeName = activation.GrainInstanceType.FullName;

                // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
                if (logger.IsVerbose) logger.Verbose(ErrorCode.Catalog_BeforeCallingDeactivate, "About to call {1} grain's OnDeactivateAsync() method {0}", activation, grainTypeName);

                // Call OnDeactivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
                try
                {
                    // just check in case this activation data is already Invalid or not here at all.
                    ActivationData ignore;
                    if (TryGetActivationData(activation.ActivationId, out ignore) &&
                        activation.State == ActivationState.Deactivating)
                    {
                        RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake. 
                        await activation.GrainInstance.OnDeactivateAsync();
                    }
                    if (logger.IsVerbose) logger.Verbose(ErrorCode.Catalog_AfterCallingDeactivate, "Returned from calling {1} grain's OnDeactivateAsync() method {0}", activation, grainTypeName);
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

        private async Task RegisterActivationInGrainDirectoryAndValidate(ActivationData activation)
        {
            ActivationAddress address = activation.Address;
            // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker. 
            // Among those that are registered in the directory, we currently do not have any multi activations.
            if (activation.IsUsingGrainDirectory)
            {
                
                var result = await scheduler.RunOrQueueTask(() => directory.RegisterAsync(address, singleActivation:true), this.SchedulingContext);
                if (address.Equals(result.Address)) return;
               
                SiloAddress primaryDirectoryForGrain = directory.GetPrimaryForGrain(address.Grain);
                throw new DuplicateActivationException(result.Address, primaryDirectoryForGrain);
            }
            else
            {
                StatelessWorkerPlacement stPlacement = activation.PlacedUsing as StatelessWorkerPlacement;
                int maxNumLocalActivations = stPlacement.MaxLocal;
                lock (activations)
                {
                    List<ActivationData> local;
                    if (!LocalLookup(address.Grain, out local) || local.Count <= maxNumLocalActivations)
                        return;

                    var id = StatelessWorkerDirector.PickRandom(local).Address;
                    throw new DuplicateActivationException(id);
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
#region IPlacementContext

        public Logger Logger => logger;

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

        public Task CreateSystemGrain(GrainId grainId, string grainType)
        {
            ActivationAddress target = ActivationAddress.NewActivationAddress(LocalSilo, grainId);
            Task activatedPromise;
            GetOrCreateActivation(target, true, grainType, null, null, out activatedPromise);
            return activatedPromise ?? TaskDone.Done;
        }


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

#region Implementation of ISiloStatusListener

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // ignore joining events and also events on myself.
            if (updatedSilo.Equals(LocalSilo)) return;

            // We deactivate those activations when silo goes either of ShuttingDown/Stopping/Dead states,
            // since this is what Directory is doing as well. Directory removes a silo based on all those 3 statuses,
            // thus it will only deliver a "remove" notification for a given silo once to us. Therefore, we need to react the fist time we are notified.
            // We may review the directory behaiviour in the future and treat ShuttingDown differently ("drain only") and then this code will have to change a well.
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
                                // adapted from InsideGarinClient.DeactivateOnIdle().
                                activationData.ResetKeepAliveRequest();
                                activationsToShutdown.Add(activationData);
                            }
                        }
                        catch (Exception exc)
                        {
                            logger.Error(ErrorCode.Catalog_SiloStatusChangeNotification_Exception,
                                String.Format("Catalog has thrown an exception while executing SiloStatusChangeNotification of silo {0}.", updatedSilo.ToStringWithHashCode()), exc);
                        }
                    }
                }
                logger.Info(ErrorCode.Catalog_SiloStatusChangeNotification,
                    String.Format("Catalog is deactivating {0} activations due to a failure of silo {1}, since it is a primary directory partiton to these grain ids.",
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

#endregion
    }
}
