using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.MultiCluster;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans.Runtime
{
    internal class Catalog : SystemTarget, ICatalog, IPlacementRuntime, IHealthCheckParticipant
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

        public SiloAddress LocalSilo { get; private set; }
        internal ISiloStatusOracle SiloStatusOracle { get; set; }
        private readonly ActivationCollector activationCollector;

        private static readonly TimeSpan UnregisterTimeout = TimeSpan.FromSeconds(1);

        private readonly IGrainLocator grainLocator;
        private readonly GrainTypeManager grainTypeManager;
        private readonly IGrainDirectoryResolver grainDirectoryResolver;
        private readonly ILocalGrainDirectory directory;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ActivationDirectory activations;
        private IStreamProviderRuntime providerRuntime;
        private IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private int collectionNumber;
        private IAsyncTimer gcTimer;
        private Task gcTimerTask;
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
        private readonly RuntimeMessagingTrace messagingTrace;

        public Catalog(
            ILocalSiloDetails localSiloDetails,
            IGrainLocator grainLocator,
            IGrainDirectoryResolver grainDirectoryResolver,
            ILocalGrainDirectory grainDirectory,
            GrainTypeManager typeManager,
            OrleansTaskScheduler scheduler,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            GrainCreator grainCreator,
            MessageCenter messageCenter,
            PlacementDirectorsManager placementDirectorsManager,
            MessageFactory messageFactory,
            SerializationManager serializationManager,
            IStreamProviderRuntime providerRuntime,
            IServiceProvider serviceProvider,
            CachedVersionSelectorManager versionSelectorManager,
            ILoggerFactory loggerFactory,
            IOptions<SchedulingOptions> schedulingOptions,
            IOptions<GrainCollectionOptions> collectionOptions,
            IOptions<SiloMessagingOptions> messagingOptions,
            RuntimeMessagingTrace messagingTrace,
            IAsyncTimerFactory timerFactory)
            : base(Constants.CatalogType, messageCenter.MyAddress, loggerFactory)
        {
            this.LocalSilo = localSiloDetails.SiloAddress;
            this.localSiloName = localSiloDetails.Name;
            this.grainLocator = grainLocator;
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.directory = grainDirectory;
            this.activations = activationDirectory;
            this.scheduler = scheduler;
            this.loggerFactory = loggerFactory;
            this.grainTypeManager = typeManager;
            this.collectionNumber = 0;
            this.grainCreator = grainCreator;
            this.serializationManager = serializationManager;
            this.versionSelectorManager = versionSelectorManager;
            this.providerRuntime = providerRuntime;
            this.serviceProvider = serviceProvider;
            this.collectionOptions = collectionOptions;
            this.messagingOptions = messagingOptions;
            this.messagingTrace = messagingTrace;
            this.logger = loggerFactory.CreateLogger<Catalog>();
            this.activationCollector = activationCollector;
            this.Dispatcher = new Dispatcher(
                scheduler,
                messageCenter,
                this,
                this.messagingOptions,
                placementDirectorsManager,
                grainDirectory,
                grainLocator,
                this.activationCollector,
                messageFactory,
                versionSelectorManager.CompatibilityDirectorManager,
                loggerFactory,
                schedulingOptions,
                messagingTrace,
                grainTypeManager);
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
            this.gcTimer = timerFactory.Create(this.activationCollector.Quantum, "Catalog.GCTimer");
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

            var typeCode = ((LegacyGrainId)target.GrainIdentity).TypeCode;
            var silos = target.InterfaceVersion > 0
                ? versionSelectorManager.GetSuitableSilos(typeCode, target.InterfaceId, target.InterfaceVersion).SuitableSilos
                : grainTypeManager.GetSupportedSilos(typeCode);

            var compatibleSilos = silos.Intersect(AllActiveSilos).ToList();
            if (compatibleSilos.Count == 0)
                throw new OrleansException($"TypeCode ${typeCode} not supported in the cluster");

            return compatibleSilos;
        }

        public IReadOnlyDictionary<ushort, IReadOnlyList<SiloAddress>> GetCompatibleSilosWithVersions(PlacementTarget target)
        {
            if (target.InterfaceVersion == 0)
                throw new ArgumentException("Interface version not provided", nameof(target));

            var typeCode = ((LegacyGrainId)target.GrainIdentity).TypeCode;
            var silos = versionSelectorManager
                .GetSuitableSilos(typeCode, target.InterfaceId, target.InterfaceVersion)
                .SuitableSilosByVersion;

            return silos;
        }

        internal void Start()
        {
            this.gcTimerTask = this.RunActivationCollectionLoop();
        }

        internal async Task Stop()
        {
            this.gcTimer?.Dispose();

            if (this.gcTimerTask is Task task) await task;
        }

        private async Task RunActivationCollectionLoop()
        {
            while (await this.gcTimer.NextTick())
            {
                try
                {
                    await this.CollectActivationsImpl(true);
                }
                catch (Exception exception)
                {
                    this.logger.LogError(exception, "Exception while collecting activations: {Exception}", exception);
                }
            }
        }

        public Task CollectActivations(TimeSpan ageLimit)
        {
            return CollectActivationsImpl(false, ageLimit);
        }

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit = default(TimeSpan))
        {
            var watch = ValueStopwatch.StartNew();
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
                            GrainIdentity = (LegacyGrainId)data.Grain,
                            SiloAddress = data.Silo,
                            Category = ((LegacyGrainId)data.Grain).Category.ToString()
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
                string grainClassName;
                grainTypeManager.GetTypeInfo(((LegacyGrainId)grain).TypeCode, out grainClassName, out unused);
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

        /// <summary>
        /// Register a new object to which messages can be delivered with the local lookup table and scheduler.
        /// </summary>
        /// <param name="activation"></param>
        public void RegisterMessageTarget(ActivationData activation)
        {
            scheduler.RegisterWorkContext(activation);
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

            scheduler.UnregisterWorkContext(activation);

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

        internal bool CanInterleave(ActivationId running, Message message)
        {
            ActivationData target;
            GrainTypeData data;
            return TryGetActivationData(running, out target) &&
                target.GrainInstance != null &&
                grainTypeManager.TryGetData(TypeUtils.GetFullName(target.GrainInstanceType), out data) &&
                (data.IsReentrant || data.MayInterleave((InvokeMethodRequest)message.BodyObject));
        }

        public void GetGrainTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            grainTypeManager.GetTypeInfo(typeCode, out grainClass, out placement, genericArguments);
        }

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
                
                int typeCode = ((LegacyGrainId)address.Grain).TypeCode;
                string actualGrainType = null;

                if (typeCode != 0)
                {
                    GetGrainTypeInfo(typeCode, out actualGrainType, out placement, genericArguments);
                    if (string.IsNullOrEmpty(grainType))
                    {
                        grainType = actualGrainType;
                    }
                }
                else
                {
                    // special case for Membership grain.
                    placement = SystemPlacement.Singleton;
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

            try
            {
                SetupActivationInstance(result, grainType, genericArguments);
            }
            catch
            {
                // Exception was thrown when trying to constuct the grain
                UnregisterMessageTarget(result);
                throw;
            }
            activatedPromise = InitActivation(result, requestContextData);
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

        private async Task InitActivation(ActivationData activation, Dictionary<string, object> requestContextData)
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
                    await RecoverFailedInitActivation(activation, initStage, registrationResult);
                    return;
                }

                initStage = ActivationInitializationStage.InvokeActivate;
                await InvokeActivate(activation, requestContextData);

                this.activationCollector.ScheduleCollection(activation);

                // Success!! Log the result, and start processing messages
                initStage = ActivationInitializationStage.Completed;
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("InitActivation is done: {0}", activation.Address);
            }
            catch (Exception ex)
            {
                await RecoverFailedInitActivation(activation, initStage, exception: ex);
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
        private async Task RecoverFailedInitActivation(
            ActivationData activation,
            ActivationInitializationStage initStage,
            ActivationRegistrationResult registrationResult = default(ActivationRegistrationResult),
            Exception exception = null)
        {
            var address = activation.Address;

            if (initStage == ActivationInitializationStage.Register && registrationResult.ExistingActivationAddress != null)
            {
                // Another activation is registered in the directory: let's forward everything
                lock (activation)
                {
                    activation.SetState(ActivationState.Invalid);
                    activation.ForwardingAddress = registrationResult.ExistingActivationAddress;
                    if (activation.ForwardingAddress != null)
                    {
                        CounterStatistic
                            .FindOrCreate(StatisticNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS)
                            .Increment();
                        var primary = directory.GetPrimaryForGrain(activation.ForwardingAddress.Grain);
                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            // If this was a duplicate, it's not an error, just a race.
                            // Forward on all of the pending messages, and then forget about this activation.
                            var logMsg =
                                $"Tried to create a duplicate activation {address}, but we'll use {activation.ForwardingAddress} instead. " +
                                $"GrainInstanceType is {activation.GrainInstanceType}. " +
                                $"{(primary != null ? "Primary Directory partition for this grain is " + primary + ". " : string.Empty)}" +
                                $"Full activation address is {address.ToFullString()}. We have {activation.WaitingCount} messages to forward.";
                            logger.Debug(ErrorCode.Catalog_DuplicateActivation, logMsg);
                        }

                        UnregisterMessageTarget(activation);
                        RerouteAllQueuedMessages(activation, activation.ForwardingAddress, "Duplicate activation", exception);
                    }
                }
            }
            else
            {
                // Before anything let's unregister the activation from the directory, so other silo don't keep sending message to it
                if (activation.IsUsingGrainDirectory)
                {
                    try
                    {
                        await this.scheduler.RunOrQueueTask(
                                    () => this.grainLocator.Unregister(address, UnregistrationCause.Force),
                                    this).WithTimeout(UnregisterTimeout);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(
                            ErrorCode.Catalog_UnregisterAsync,
                            $"Failed to unregister activation {activation} after {initStage} stage failed",
                            ex);
                    }
                }
                lock (activation)
                {
                    UnregisterMessageTarget(activation);
                    if (initStage == ActivationInitializationStage.InvokeActivate)
                    {
                        activation.SetState(ActivationState.FailedToActivate);
                        logger.Warn(ErrorCode.Catalog_Failed_InvokeActivate, string.Format("Failed to InvokeActivate for {0}.", activation), exception);
                        // Reject all of the messages queued for this activation.
                        var activationFailedMsg = nameof(Grain.OnActivateAsync) + " failed";
                        RejectAllQueuedMessages(activation, activationFailedMsg, exception);
                    }
                    else
                    {
                        activation.SetState(ActivationState.Invalid);
                        logger.Warn(ErrorCode.Runtime_Error_100064, $"Failed to RegisterActivationInGrainDirectory for {activation}.", exception);
                        RerouteAllQueuedMessages(activation, null, "Failed RegisterActivationInGrainDirectory", exception);
                    }
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
            if (!grainTypeManager.TryGetPrimaryImplementation(grainTypeName, out grainClassName))
            {
                // Lookup from grain type code
                var typeCode = ((LegacyGrainId)data.Grain).TypeCode;
                if (typeCode != 0)
                {
                    PlacementStrategy unused;
                    GetGrainTypeInfo(typeCode, out grainClassName, out unused, genericArguments);
                }
                else
                {
                    grainClassName = grainTypeName;
                }
            }

            GrainTypeData grainTypeData = grainTypeManager[grainClassName];

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
            var invoker = InsideRuntimeClient.TryGetExtensionMethodInvoker(this.grainTypeManager, typeof(IStreamConsumerExtension));
            if (invoker == null)
                throw new InvalidOperationException("Extension method invoker was not generated for an extension interface");
            var handler = new StreamConsumerExtension(this.providerRuntime, observer);
            result.ExtensionInvoker.TryAddExtension(invoker, handler);
        }

        /// <summary>
        /// Try to get runtime data for an activation
        /// </summary>
        /// <param name="activationId"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool TryGetActivationData(ActivationId activationId, out ActivationData data)
        {
            data = activations.FindTarget(activationId);
            return data != null;
        }

        private async Task DeactivateActivationsFromCollector(List<ActivationData> list)
        {
            var cts = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);
            var mtcs = new MultiTaskCompletionSource(list.Count);

            logger.Info(ErrorCode.Catalog_ShutdownActivations_1, "DeactivateActivationsFromCollector: total {0} to promptly Destroy.", list.Count);
            CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION).IncrementBy(list.Count);

            for (var i = 0; i < list.Count; i++)
            {
                var activationData = list[i];
                lock (activationData)
                {
                    // Continue deactivation when ready
                    activationData.AddOnInactive(async () =>
                    {
                        try
                        {
                            await DestroyActivation(activationData, cts.Token);
                        }
                        finally
                        {
                            mtcs.SetOneResult();
                        }
                    });
                }
            }

            await mtcs.Task;
        }

        // To be called fro within Activation context.
        // Cannot be awaitable, since after DestroyActivation is done the activation is in Invalid state and cannot await any Task.
        internal void DeactivateActivationOnIdle(ActivationData data)
        {
            DeactivateActivationImpl(data, StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE);
        }

        // To be called from within Activation context.
        // To be used only if an activation is stuck for a long time, since it can lead to a duplicate activation
        internal void DeactivateStuckActivation(ActivationData activationData)
        {
            DeactivateActivationImpl(activationData, StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_STUCK_ACTIVATION);
            // The unregistration is normally done in the regular deactivation process, but since this activation seems
            // stuck (it might never run the deactivation process), we remove it from the directory directly
            scheduler.RunOrQueueTask(
                () => this.grainLocator.Unregister(activationData.Address, UnregistrationCause.Force),
                this)
                .Ignore();
        }

        private void DeactivateActivationImpl(ActivationData data, StatisticName statisticName)
        {
            var cts = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);
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
                        data.AddOnInactive(() => _ = DestroyActivation(data, cts.Token));
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
                _ = DestroyActivation(data, cts.Token); // Don't await or Ignore, since we are in this activation context and it may have alraedy been destroyed!
            }
        }

        internal async Task DeactivateActivation(ActivationData activationData)
        {
            TaskCompletionSource<object> tcs;
            var cts = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);

            lock (activationData)
            {
                if (activationData.State != ActivationState.Valid)
                    return; // Nothing to do

                tcs = new TaskCompletionSource<object>();

                // Don't accept any new messages
                activationData.PrepareForDeactivation(); 
                this.activationCollector.TryCancelCollection(activationData);

                // Continue deactivation when ready
                activationData.AddOnInactive(async () =>
                {
                    try
                    {
                        await DestroyActivation(activationData, cts.Token);
                    }
                    finally
                    {
                        tcs.SetResult(null);
                    }
                });
            }

            await tcs.Task.WithCancellation(cts.Token);
        }

        private async Task DestroyActivation(ActivationData activationData, CancellationToken ct)
        {
            try
            {
                // Wait timers and call OnDeactivateAsync()
                await activationData.WaitForAllTimersToFinish();
                await this.scheduler.RunOrQueueTask(() => CallGrainDeactivateAndCleanupStreams(activationData, ct), activationData);
                // Unregister from directory
                await this.grainLocator.Unregister(activationData.Address, UnregistrationCause.Force);
            }
            catch (Exception ex)
            {
                this.logger.Warn(ErrorCode.Catalog_DeactivateActivation_Exception, $"Exception when trying to deactivation {activationData}", ex);
            }
            finally
            {
                lock (activationData)
                {
                    activationData.SetState(ActivationState.Invalid);
                }
                // Capture grainInstance since UnregisterMessageTarget will set it to null...
                var grainInstance = activationData.GrainInstance;
                UnregisterMessageTarget(activationData);
                RerouteAllQueuedMessages(activationData, null, "Finished Destroy Activation");
                if (grainInstance != null)
                {
                    lock (activationData)
                    {
                        grainCreator.Release(activationData, grainInstance);
                    }
                }
                activationData.Dispose();
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

            await Task.WhenAll(list.Select(DeactivateActivation));
        }

        public Task DeactivateAllActivations()
        {
            logger.Info(ErrorCode.Catalog_DeactivateAllActivations, "DeactivateAllActivations.");
            var activationsToShutdown = activations.Where(kv => !kv.Value.IsExemptFromCollection).Select(kv => kv.Value).ToList();
            return DeactivateActivations(activationsToShutdown);
        }

        private void RerouteAllQueuedMessages(ActivationData activation, ActivationAddress forwardingAddress, string failedOperation, Exception exc = null)
        {
            lock (activation)
            {
                List<Message> msgs = activation.DequeueAllWaitingMessages();
                if (msgs == null || msgs.Count <= 0) return;

                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_RerouteAllQueuedMessages, String.Format("RerouteAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count, activation));
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
                        string.Format("RejectAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count, activation));
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

                activationsFailedToActivate.Increment();

                // TODO: During lifecycle refactor discuss with team whether activation failure should have a well defined exception, or throw whatever
                //   exception caused activation to fail, with no indication that it occured durring activation
                //   rather than the grain call.
                var canceledException = exc as OrleansLifecycleCanceledException;
                if(canceledException?.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(canceledException.InnerException).Throw();
                }
                throw;
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        private async Task<ActivationData> CallGrainDeactivateAndCleanupStreams(ActivationData activation, CancellationToken ct)
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
                        await activation.Lifecycle.OnStop().WithCancellation(ct);
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
            /// Returns true if this instance represents a successful registration, false otherwise.
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
            var address = activation.Address;

            // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker. 
            // Among those that are registered in the directory, we currently do not have any multi activations.
            if (activation.IsUsingGrainDirectory)
            {
                var result = await scheduler.RunOrQueueTask(() => this.grainLocator.Register(address), this);
                if (address.Equals(result)) return ActivationRegistrationResult.Success;
               
                return new ActivationRegistrationResult(existingActivationAddress: result);
            }
            else if (activation.PlacedUsing is StatelessWorkerPlacement stPlacement)
            {
                // Stateless workers are not registered in the directory and can have multiple local activations.
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
            else
            {
                // Some other non-directory, single-activation placement.
                lock (activations)
                {
                    var exists = LocalLookup(address.Grain, out var local);
                    if (exists && local.Count == 1 && local[0].ActivationId.Equals(activation.ActivationId))
                    {
                        return ActivationRegistrationResult.Success;
                    }

                    return new ActivationRegistrationResult(existingActivationAddress: local[0].Address);
                }
            }

            // We currently don't have any other case for multiple activations except for StatelessWorker. 
        }

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
            return scheduler.QueueTask(() => CallGrainActivate(activation, requestContextData), activation); // Target grain);
            // ActivationData will transition out of ActivationState.Activating via Dispatcher.OnActivationCompletedRequest
        }

        public bool FastLookup(GrainId grain, out List<ActivationAddress> addresses)
        {
            return this.grainLocator.TryLocalLookup(grain, out addresses) && addresses != null && addresses.Count > 0;
            // NOTE: only check with the local directory cache.
            // DO NOT check in the local activations TargetDirectory!!!
            // The only source of truth about which activation should be legit to is the state of the ditributed directory.
            // Everyone should converge to that (that is the meaning of "eventualy consistency - eventualy we converge to one truth").
            // If we keep using the local activation, it may not be registered in th directory any more, but we will never know that and keep using it,
            // thus volaiting the single-activation semantics and not converging even eventualy!
        }

        public Task<List<ActivationAddress>> FullLookup(GrainId grain)
        {
            return scheduler.RunOrQueueTask(() => this.grainLocator.Lookup(grain), this);
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
                var result = SiloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
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

        public Task DeleteActivations(List<ActivationAddress> addresses)
        {
            List<ActivationData> TryGetActivationDatas(List<ActivationAddress> addresses)
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

            var timeoutTokenSource = new CancellationTokenSource(this.collectionOptions.Value.DeactivationTimeout);
            var tasks = new List<Task>(addresses.Count);
            foreach (var activationData in TryGetActivationDatas(addresses))
            {
                var capture = activationData;
                tasks.Add(DestroyActivation(capture, timeoutTokenSource.Token));
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
                            if (!activationData.IsUsingGrainDirectory || grainDirectoryResolver.Resolve(activationData.Grain) != default) continue;
                            if (!updatedSilo.Equals(directory.GetPrimaryForGrain(activationData.Grain))) continue;

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

        public bool CheckHealth(DateTime lastCheckTime)
        {
            if (this.gcTimer is IAsyncTimer timer)
            {
                return timer.CheckHealth(lastCheckTime);
            }

            return true;
        }
    }
}
