using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Startup;
using Orleans.Runtime.Storage;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{

    /// <summary>
    /// Orleans silo.
    /// </summary>
    public class Silo : MarshalByRefObject // for hosting multiple silos in app domains of the same process
    {
        /// <summary> Silo Types. </summary>
        public enum SiloType
        {
            None = 0,
            Primary,
            Secondary,
        }

        /// <summary> Type of this silo. </summary>
        public SiloType Type
        {
            get { return siloType; }
        }

        private readonly GlobalConfiguration globalConfig;
        private NodeConfiguration nodeConfig;
        private readonly ISiloMessageCenter messageCenter;
        private readonly OrleansTaskScheduler scheduler;
        private readonly LocalGrainDirectory localGrainDirectory;
        private readonly ActivationDirectory activationDirectory;
        private readonly IncomingMessageAgent incomingAgent;
        private readonly IncomingMessageAgent incomingSystemAgent;
        private readonly IncomingMessageAgent incomingPingAgent;
        private readonly TraceLogger logger;
        private readonly GrainTypeManager typeManager;
        private readonly ManualResetEvent siloTerminatedEvent;
        private readonly SiloType siloType;
        private readonly SiloStatisticsManager siloStatistics;
        private readonly MembershipFactory membershipFactory;
        private StorageProviderManager storageProviderManager;
        private StatisticsProviderManager statisticsProviderManager;
        private BootstrapProviderManager bootstrapProviderManager;
        private readonly LocalReminderServiceFactory reminderFactory;
        private IReminderService reminderService;
        private ProviderManagerSystemTarget providerManagerSystemTarget;
        private IMembershipOracle membershipOracle;
        private ClientObserverRegistrar clientRegistrar;
        private Watchdog platformWatchdog;
        private readonly TimeSpan initTimeout;
        private readonly TimeSpan stopTimeout = TimeSpan.FromMinutes(1);
        private readonly Catalog catalog;
        private readonly List<IHealthCheckParticipant> healthCheckParticipants;
        private readonly object lockable = new object();
        private readonly GrainFactory grainFactory;
        private readonly IGrainRuntime grainRuntime;
        private readonly List<IProvider> allSiloProviders;
        private readonly IServiceProvider services;
        
        internal readonly string Name;
        internal readonly string SiloIdentity;
        internal ClusterConfiguration OrleansConfig { get; private set; }
        internal GlobalConfiguration GlobalConfig { get { return globalConfig; } }
        internal NodeConfiguration LocalConfig { get { return nodeConfig; } }
        internal ISiloMessageCenter LocalMessageCenter { get { return messageCenter; } }
        internal OrleansTaskScheduler LocalScheduler { get { return scheduler; } }
        internal GrainTypeManager LocalTypeManager { get { return typeManager; } }
        internal ILocalGrainDirectory LocalGrainDirectory { get { return localGrainDirectory; } }
        internal ISiloStatusOracle LocalSiloStatusOracle { get { return membershipOracle; } }
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal IStorageProviderManager StorageProviderManager { get { return storageProviderManager; } }
        internal IProviderManager StatisticsProviderManager { get { return statisticsProviderManager; } }
        internal IList<IBootstrapProvider> BootstrapProviders { get; private set; }
        internal ISiloPerformanceMetrics Metrics { get { return siloStatistics.MetricsTable; } }
        internal static Silo CurrentSilo { get; private set; }
        internal IReadOnlyCollection<IProvider> AllSiloProviders 
        {
            get { return allSiloProviders.AsReadOnly();  }
        }

        internal IServiceProvider Services { get { return services; } }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress { get { return messageCenter.MyAddress; } }

        /// <summary>
        ///  Silo termination event used to signal shutdown of this silo.
        /// </summary>
        public WaitHandle SiloTerminatedEvent { get { return siloTerminatedEvent; } } // one event for all types of termination (shutdown, stop and fast kill).

        /// <summary>
        /// Test hook connection for white-box testing of silo.
        /// </summary>
        public TestHooks TestHook;
        
        /// <summary>
        /// Creates and initializes the silo from the specified config data.
        /// </summary>
        /// <param name="name">Name of this silo.</param>
        /// <param name="siloType">Type of this silo.</param>
        /// <param name="config">Silo config data to be used for this silo.</param>
        public Silo(string name, SiloType siloType, ClusterConfiguration config)
            : this(name, siloType, config, null)
        {}

        /// <summary>
        /// Creates and initializes the silo from the specified config data.
        /// </summary>
        /// <param name="name">Name of this silo.</param>
        /// <param name="siloType">Type of this silo.</param>
        /// <param name="config">Silo config data to be used for this silo.</param>
        /// <param name="keyStore">Local data store, mostly used for testing, shared between all silos running in same process.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Should not Dispose of messageCenter in this method because it continues to run / exist after this point.")]
        internal Silo(string name, SiloType siloType, ClusterConfiguration config, ILocalDataStore keyStore)
        {
            SystemStatus.Current = SystemStatus.Creating;

            CurrentSilo = this;

            var startTime = DateTime.UtcNow;

            this.siloType = siloType;
            Name = name;

            siloTerminatedEvent = new ManualResetEvent(false);

            OrleansConfig = config;
            globalConfig = config.Globals;
            config.OnConfigChange("Defaults", () => nodeConfig = config.GetConfigurationForNode(name));

            if (!TraceLogger.IsInitialized)
                TraceLogger.Initialize(nodeConfig);

            config.OnConfigChange("Defaults/Tracing", () => TraceLogger.Initialize(nodeConfig, true), false);

            ActivationData.Init(config, nodeConfig);
            StatisticsCollector.Initialize(nodeConfig);
            
            CodeGeneratorManager.GenerateAndCacheCodeForAllAssemblies();
            SerializationManager.Initialize(globalConfig.UseStandardSerializer, globalConfig.SerializationProviders, globalConfig.UseJsonFallbackSerializer);
            initTimeout = globalConfig.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), globalConfig.MaxJoinAttemptTime);
                stopTimeout = initTimeout;
            }

            IPEndPoint here = nodeConfig.Endpoint;
            int generation = nodeConfig.Generation;
            if (generation == 0)
            {
                generation = SiloAddress.AllocateNewGeneration();
                nodeConfig.Generation = generation;
            }
            TraceLogger.MyIPEndPoint = here;
            logger = TraceLogger.GetLogger("Silo", TraceLogger.LoggerType.Runtime);

            logger.Info(ErrorCode.SiloGcSetting, "Silo starting with GC settings: ServerGC={0} GCLatencyMode={1}", GCSettings.IsServerGC, Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode));
            if (!GCSettings.IsServerGC || !GCSettings.LatencyMode.Equals(GCLatencyMode.Batch))
                logger.Warn(ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on or with GCLatencyMode.Batch enabled - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\"> and <configuration>-<runtime>-<gcConcurrent enabled=\"false\"/>");

            logger.Info(ErrorCode.SiloInitializing, "-------------- Initializing {0} silo on host {1} MachineName {2} at {3}, gen {4} --------------",
                siloType, nodeConfig.DNSHostName, Environment.MachineName, here, generation);
            logger.Info(ErrorCode.SiloInitConfig, "Starting silo {0} with runtime Version='{1}' .NET version='{2}' Is .NET 4.5={3} OS version='{4}' Config= " + Environment.NewLine + "{5}",
                name, RuntimeVersion.Current, Environment.Version, ConfigUtilities.IsNet45OrNewer(), Environment.OSVersion, config.ToString(name));

            if (keyStore != null)
            {
                // Re-establish reference to shared local key store in this app domain
                LocalDataStoreInstance.LocalDataStore = keyStore;
            }

            services = new DefaultServiceProvider();
            var startupBuilder = AssemblyLoader.TryLoadAndCreateInstance<IStartupBuilder>("OrleansDependencyInjection", logger);
            if (startupBuilder != null)
            {
                logger.Info(ErrorCode.SiloLoadedDI, "Successfully loaded {0} from OrleansDependencyInjection.dll", startupBuilder.GetType().FullName);
                try
                {
                    services = startupBuilder.ConfigureStartup(nodeConfig.StartupTypeName);
                }
                catch (FileNotFoundException exc)
                {
                    logger.Warn(ErrorCode.SiloFileNotFoundLoadingDI, "Caught a FileNotFoundException calling ConfigureStartup(). Ignoring it. {0}", exc);
                }
            }
            else
            {
                logger.Warn(ErrorCode.SiloFailedToLoadDI, "Failed to load an implementation of IStartupBuilder from OrleansDependencyInjection.dll");
            }

            healthCheckParticipants = new List<IHealthCheckParticipant>();
            allSiloProviders = new List<IProvider>();

            BufferPool.InitGlobalBufferPool(globalConfig);
            PlacementStrategy.Initialize(globalConfig);

            UnobservedExceptionsHandlerClass.SetUnobservedExceptionHandler(UnobservedExceptionHandler);
            AppDomain.CurrentDomain.UnhandledException +=
                (obj, ev) => DomainUnobservedExceptionHandler(obj, (Exception)ev.ExceptionObject);

            grainFactory = new GrainFactory();
            typeManager = new GrainTypeManager(here.Address.Equals(IPAddress.Loopback), grainFactory);

            // Performance metrics
            siloStatistics = new SiloStatisticsManager(globalConfig, nodeConfig);
            config.OnConfigChange("Defaults/LoadShedding", () => siloStatistics.MetricsTable.NodeConfig = nodeConfig, false);

            // The scheduler
            scheduler = new OrleansTaskScheduler(globalConfig, nodeConfig);
            healthCheckParticipants.Add(scheduler);

            // Initialize the message center
            var mc = new MessageCenter(here, generation, globalConfig, siloStatistics.MetricsTable);
            if (nodeConfig.IsGatewayNode)
                mc.InstallGateway(nodeConfig.ProxyGatewayEndpoint);
            
            messageCenter = mc;

            SiloIdentity = SiloAddress.ToLongString();

            // GrainRuntime can be created only here, after messageCenter was created.
            grainRuntime = new GrainRuntime(
                globalConfig.ServiceId,
                SiloIdentity, 
                grainFactory,
                new TimerRegistry(),
                new ReminderRegistry(),
                new StreamProviderManager(),
                Services);


            // Now the router/directory service
            // This has to come after the message center //; note that it then gets injected back into the message center.;
            localGrainDirectory = new LocalGrainDirectory(this); 

            // Now the activation directory.
            // This needs to know which router to use so that it can keep the global directory in synch with the local one.
            activationDirectory = new ActivationDirectory();
            
            // Now the consistent ring provider
            RingProvider = GlobalConfig.UseVirtualBucketsConsistentRing ?
                (IConsistentRingProvider) new VirtualBucketsRingProvider(SiloAddress, GlobalConfig.NumVirtualBucketsConsistentRing)
                : new ConsistentRingProvider(SiloAddress);

            Action<Dispatcher> setDispatcher;
            catalog = new Catalog(Constants.CatalogId, SiloAddress, Name, LocalGrainDirectory, typeManager, scheduler, activationDirectory, config, grainRuntime, out setDispatcher);
            var dispatcher = new Dispatcher(scheduler, messageCenter, catalog, config);
            setDispatcher(dispatcher);

            RuntimeClient.Current = new InsideRuntimeClient(
                dispatcher, 
                catalog, 
                LocalGrainDirectory, 
                SiloAddress, 
                config, 
                RingProvider, 
                typeManager,
                grainFactory);
            messageCenter.RerouteHandler = InsideRuntimeClient.Current.RerouteMessage;
            messageCenter.SniffIncomingMessage = InsideRuntimeClient.Current.SniffIncomingMessage;

            siloStatistics.MetricsTable.Scheduler = scheduler;
            siloStatistics.MetricsTable.ActivationDirectory = activationDirectory;
            siloStatistics.MetricsTable.ActivationCollector = catalog.ActivationCollector;
            siloStatistics.MetricsTable.MessageCenter = messageCenter;

            DeploymentLoadPublisher.CreateDeploymentLoadPublisher(this, globalConfig);
            PlacementDirectorsManager.CreatePlacementDirectorsManager(globalConfig);

            // Now the incoming message agents
            incomingSystemAgent = new IncomingMessageAgent(Message.Categories.System, messageCenter, activationDirectory, scheduler, dispatcher);
            incomingPingAgent = new IncomingMessageAgent(Message.Categories.Ping, messageCenter, activationDirectory, scheduler, dispatcher);
            incomingAgent = new IncomingMessageAgent(Message.Categories.Application, messageCenter, activationDirectory, scheduler, dispatcher);

            membershipFactory = new MembershipFactory();
            reminderFactory = new LocalReminderServiceFactory();
            
            SystemStatus.Current = SystemStatus.Created;

            StringValueStatistic.FindOrCreate(StatisticNames.SILO_START_TIME,
                () => TraceLogger.PrintDate(startTime)); // this will help troubleshoot production deployment when looking at MDS logs.

            TestHook = new TestHooks(this);

            logger.Info(ErrorCode.SiloInitializingFinished, "-------------- Started silo {0}, ConsistentHashCode {1:X} --------------", SiloAddress.ToLongString(), SiloAddress.GetConsistentHashCode());
        }

        private void CreateSystemTargets()
        {
            logger.Verbose("Creating System Targets for this silo.");

            logger.Verbose("Creating {0} System Target", "SiloControl");
            RegisterSystemTarget(new SiloControl(this));

            logger.Verbose("Creating {0} System Target", "DeploymentLoadPublisher");
            RegisterSystemTarget(DeploymentLoadPublisher.Instance);

            logger.Verbose("Creating {0} System Target", "RemGrainDirectory + CacheValidator");
            RegisterSystemTarget(LocalGrainDirectory.RemGrainDirectory);
            RegisterSystemTarget(LocalGrainDirectory.CacheValidator);

            logger.Verbose("Creating {0} System Target", "ClientObserverRegistrar + TypeManager");
            clientRegistrar = new ClientObserverRegistrar(SiloAddress, LocalMessageCenter, LocalGrainDirectory, LocalScheduler, OrleansConfig);
            RegisterSystemTarget(clientRegistrar);
            RegisterSystemTarget(new TypeManager(SiloAddress, LocalTypeManager));

            logger.Verbose("Creating {0} System Target", "MembershipOracle");
            RegisterSystemTarget((SystemTarget) membershipOracle);

            logger.Verbose("Finished creating System Targets for this silo.");
        }

        private void InjectDependencies()
        {
            healthCheckParticipants.Add(membershipOracle);

            catalog.SiloStatusOracle = LocalSiloStatusOracle;
            localGrainDirectory.CatalogSiloStatusListener = catalog;
            LocalSiloStatusOracle.SubscribeToSiloStatusEvents(localGrainDirectory);
            messageCenter.SiloDeadOracle = LocalSiloStatusOracle.IsDeadSilo;

            // consistentRingProvider is not a system target per say, but it behaves like the localGrainDirectory, so it is here
            LocalSiloStatusOracle.SubscribeToSiloStatusEvents((ISiloStatusListener)RingProvider);

            LocalSiloStatusOracle.SubscribeToSiloStatusEvents(DeploymentLoadPublisher.Instance);

            if (!globalConfig.ReminderServiceType.Equals(GlobalConfiguration.ReminderServiceProviderType.Disabled))
            {
                // start the reminder service system target
                reminderService = reminderFactory.CreateReminderService(this, grainFactory, initTimeout);
                RegisterSystemTarget((SystemTarget) reminderService);
            }

            RegisterSystemTarget(catalog);
            scheduler.QueueAction(catalog.Start, catalog.SchedulingContext)
                .WaitWithThrow(initTimeout);

            // SystemTarget for provider init calls
            providerManagerSystemTarget = new ProviderManagerSystemTarget(this);
            RegisterSystemTarget(providerManagerSystemTarget);
        }

        private async Task CreateSystemGrains()
        {
            if (siloType == SiloType.Primary)
                await membershipFactory.CreateMembershipTableProvider(catalog, this).WithTimeout(initTimeout);
        }

        /// <summary> Perform silo startup operations. </summary>
        public void Start()
        {
            try
            {
                DoStart();
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.SiloStartError, "Exception during Silo.Start", exc);
                throw;
            }
        }

        private void DoStart()
        {
            lock (lockable)
            {
                if (!SystemStatus.Current.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(String.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", SystemStatus.Current));
                
                SystemStatus.Current = SystemStatus.Starting;
            }

            logger.Info(ErrorCode.SiloStarting, "Silo Start()");

            // Hook up to receive notification of process exit / Ctrl-C events
            AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
            Console.CancelKeyPress += HandleProcessExit;

            ConfigureThreadPoolAndServicePointSettings();

            // This has to start first so that the directory system target factory gets loaded before we start the router.
            typeManager.Start();
            InsideRuntimeClient.Current.Start();

            // The order of these 4 is pretty much arbitrary.
            scheduler.Start();
            messageCenter.Start();
            incomingPingAgent.Start();
            incomingSystemAgent.Start();
            incomingAgent.Start();

            LocalGrainDirectory.Start();

            // Set up an execution context for this thread so that the target creation steps can use asynch values.
            RuntimeContext.InitializeMainThread();

            SiloProviderRuntime.Initialize(GlobalConfig, SiloIdentity, grainFactory, Services);
            InsideRuntimeClient.Current.CurrentStreamProviderRuntime = SiloProviderRuntime.Instance;
            statisticsProviderManager = new StatisticsProviderManager("Statistics", SiloProviderRuntime.Instance);
            string statsProviderName =  statisticsProviderManager.LoadProvider(GlobalConfig.ProviderConfigurations)
                .WaitForResultWithThrow(initTimeout);
            if (statsProviderName != null)
                LocalConfig.StatisticsProviderName = statsProviderName;
            allSiloProviders.AddRange(statisticsProviderManager.GetProviders());

            // can call SetSiloMetricsTableDataManager only after MessageCenter is created (dependency on this.SiloAddress).
            siloStatistics.SetSiloStatsTableDataManager(this, nodeConfig).WaitWithThrow(initTimeout);
            siloStatistics.SetSiloMetricsTableDataManager(this, nodeConfig).WaitWithThrow(initTimeout);

            IMembershipTable membershipTable = membershipFactory.GetMembershipTable(GlobalConfig.LivenessType, GlobalConfig.MembershipTableAssembly);
            membershipOracle = membershipFactory.CreateMembershipOracle(this, membershipTable);
            
            // This has to follow the above steps that start the runtime components
            CreateSystemTargets();

            InjectDependencies();

            // Validate the configuration.
            GlobalConfig.Application.ValidateConfiguration(logger);

            // ensure this runs in the grain context, wait for it to complete
            scheduler.QueueTask(CreateSystemGrains, catalog.SchedulingContext)
                .WaitWithThrow(initTimeout);
            if (logger.IsVerbose) {  logger.Verbose("System grains created successfully."); }

            // Initialize storage providers once we have a basic silo runtime environment operating
            storageProviderManager = new StorageProviderManager(grainFactory, Services);
            scheduler.QueueTask(
                () => storageProviderManager.LoadStorageProviders(GlobalConfig.ProviderConfigurations),
                providerManagerSystemTarget.SchedulingContext)
                    .WaitWithThrow(initTimeout);
            catalog.SetStorageManager(storageProviderManager);
            allSiloProviders.AddRange(storageProviderManager.GetProviders());
            if (logger.IsVerbose) { logger.Verbose("Storage provider manager created successfully."); }

            // Load and init stream providers before silo becomes active
            var siloStreamProviderManager = (StreamProviderManager) grainRuntime.StreamProviderManager;
            scheduler.QueueTask(
                () => siloStreamProviderManager.LoadStreamProviders(this.GlobalConfig.ProviderConfigurations, SiloProviderRuntime.Instance),
                    providerManagerSystemTarget.SchedulingContext)
                        .WaitWithThrow(initTimeout);
            InsideRuntimeClient.Current.CurrentStreamProviderManager = siloStreamProviderManager;
            allSiloProviders.AddRange(siloStreamProviderManager.GetProviders());
            if (logger.IsVerbose) { logger.Verbose("Stream provider manager created successfully."); }

            ISchedulingContext statusOracleContext = ((SystemTarget)LocalSiloStatusOracle).SchedulingContext;

            bool waitForPrimaryToStart = globalConfig.PrimaryNodeIsRequired && siloType != SiloType.Primary;
            if (waitForPrimaryToStart) // only in MembershipTableGrain case.
            {
                scheduler.QueueTask(() => membershipFactory.WaitForTableToInit(membershipTable), statusOracleContext)
                        .WaitWithThrow(initTimeout);
            }
            scheduler.QueueTask(() => membershipTable.InitializeMembershipTable(this.GlobalConfig, true, TraceLogger.GetLogger(membershipTable.GetType().Name)), statusOracleContext)
                .WaitWithThrow(initTimeout);
          
            scheduler.QueueTask(() => LocalSiloStatusOracle.Start(), statusOracleContext)
                .WaitWithThrow(initTimeout);
            if (logger.IsVerbose) { logger.Verbose("Local silo status oracle created successfully."); }
            scheduler.QueueTask(LocalSiloStatusOracle.BecomeActive, statusOracleContext)
                .WaitWithThrow(initTimeout);
            if (logger.IsVerbose) { logger.Verbose("Local silo status oracle became active successfully."); }

            try
            {
                siloStatistics.Start(LocalConfig);
                if (logger.IsVerbose) { logger.Verbose("Silo statistics manager started successfully."); }

                // Finally, initialize the deployment load collector, for grains with load-based placement
                scheduler.QueueTask(DeploymentLoadPublisher.Instance.Start, DeploymentLoadPublisher.Instance.SchedulingContext)
                    .WaitWithThrow(initTimeout);
                if (logger.IsVerbose) { logger.Verbose("Silo deployment load publisher started successfully."); }

                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                platformWatchdog = new Watchdog(nodeConfig.StatisticsLogWriteInterval, healthCheckParticipants);
                platformWatchdog.Start();
                if (logger.IsVerbose) { logger.Verbose("Silo platform watchdog started successfully."); }

                if (reminderService != null)
                {
                    // so, we have the view of the membership in the consistentRingProvider. We can start the reminder service
                    scheduler.QueueTask(reminderService.Start, ((SystemTarget) reminderService).SchedulingContext)
                        .WaitWithThrow(initTimeout);
                    if (logger.IsVerbose)
                    {
                        logger.Verbose("Reminder service started successfully.");
                    }
                }

                bootstrapProviderManager = new BootstrapProviderManager();
                scheduler.QueueTask(
                    () => bootstrapProviderManager.LoadAppBootstrapProviders(GlobalConfig.ProviderConfigurations),
                    providerManagerSystemTarget.SchedulingContext)
                        .WaitWithThrow(initTimeout);
                BootstrapProviders = bootstrapProviderManager.GetProviders(); // Data hook for testing & diagnotics
                allSiloProviders.AddRange(BootstrapProviders);

                if (logger.IsVerbose) { logger.Verbose("App bootstrap calls done successfully."); }

                // Start stream providers after silo is active (so the pulling agents don't start sending messages before silo is active).
                // also after bootstrap provider started so bootstrap provider can initialize everything stream before events from this silo arrive.
                scheduler.QueueTask(siloStreamProviderManager.StartStreamProviders, providerManagerSystemTarget.SchedulingContext)
                    .WaitWithThrow(initTimeout);
                if (logger.IsVerbose) { logger.Verbose("Stream providers started successfully."); }

                // Now that we're active, we can start the gateway
                var mc = messageCenter as MessageCenter;
                if (mc != null)
                {
                    mc.StartGateway(clientRegistrar);
                }
                if (logger.IsVerbose) { logger.Verbose("Message gateway service started successfully."); }

                scheduler.QueueTask(clientRegistrar.Start, clientRegistrar.SchedulingContext)
                    .WaitWithThrow(initTimeout);
                if (logger.IsVerbose) { logger.Verbose("Client registrar service started successfully."); }

                SystemStatus.Current = SystemStatus.Running;
            }
            catch (Exception exc)
            {
                SafeExecute(() => logger.Error(ErrorCode.Runtime_Error_100330, String.Format("Error starting silo {0}. Going to FastKill().", SiloAddress), exc));
                FastKill(); // if failed after Membership became active, mark itself as dead in Membership abale.
                throw;
            }
            if (logger.IsVerbose) { logger.Verbose("Silo.Start complete: System status = {0}", SystemStatus.Current); }
        }

        private void ConfigureThreadPoolAndServicePointSettings()
        {
            if (nodeConfig.MinDotNetThreadPoolSize > 0)
            {
                int workerThreads;
                int completionPortThreads;
                ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
                if (nodeConfig.MinDotNetThreadPoolSize > workerThreads ||
                    nodeConfig.MinDotNetThreadPoolSize > completionPortThreads)
                {
                    // if at least one of the new values is larger, set the new min values to be the larger of the prev. and new config value.
                    int newWorkerThreads = Math.Max(nodeConfig.MinDotNetThreadPoolSize, workerThreads);
                    int newCompletionPortThreads = Math.Max(nodeConfig.MinDotNetThreadPoolSize, completionPortThreads);
                    bool ok = ThreadPool.SetMinThreads(newWorkerThreads, newCompletionPortThreads);
                    if (ok)
                    {
                        logger.Info(ErrorCode.SiloConfiguredThreadPool,
                                    "Configured ThreadPool.SetMinThreads() to values: {0},{1}. Previous values are: {2},{3}.",
                                    newWorkerThreads, newCompletionPortThreads, workerThreads, completionPortThreads);
                    }
                    else
                    {
                        logger.Warn(ErrorCode.SiloFailedToConfigureThreadPool,
                                    "Failed to configure ThreadPool.SetMinThreads(). Tried to set values to: {0},{1}. Previous values are: {2},{3}.",
                                    newWorkerThreads, newCompletionPortThreads, workerThreads, completionPortThreads);
                    }
                }
            }

            // Set .NET ServicePointManager settings to optimize throughput performance when using Azure storage
            // http://blogs.msdn.com/b/windowsazurestorage/archive/2010/06/25/nagle-s-algorithm-is-not-friendly-towards-small-requests.aspx
            logger.Info(ErrorCode.SiloConfiguredServicePointManager,
                "Configured .NET ServicePointManager to Expect100Continue={0}, DefaultConnectionLimit={1}, UseNagleAlgorithm={2} to improve Azure storage performance.",
                nodeConfig.Expect100Continue, nodeConfig.DefaultConnectionLimit, nodeConfig.UseNagleAlgorithm);
            ServicePointManager.Expect100Continue = nodeConfig.Expect100Continue;
            ServicePointManager.DefaultConnectionLimit = nodeConfig.DefaultConnectionLimit;
            ServicePointManager.UseNagleAlgorithm = nodeConfig.UseNagleAlgorithm;
        }

        /// <summary>
        /// Gracefully stop the run time system only, but not the application. 
        /// Applications requests would be abruptly terminated, while the internal system state gracefully stopped and saved as much as possible.
        /// Grains are not deactivated.
        /// </summary>
        public void Stop()
        {
            Terminate(false);
        }

        /// <summary>
        /// Gracefully stop the run time system and the application. 
        /// All grains will be properly deactivated.
        /// All in-flight applications requests would be awaited and finished gracefully.
        /// </summary>
        public void Shutdown()
        {
            Terminate(true);
        }

        /// <summary>
        /// Gracefully stop the run time system only, but not the application. 
        /// Applications requests would be abruptly terminated, while the internal system state gracefully stopped and saved as much as possible.
        /// </summary>
        private void Terminate(bool gracefully)
        {
            string operation = gracefully ? "Shutdown()" : "Stop()";
            bool stopAlreadyInProgress = false;
            lock (lockable)
            {
                if (SystemStatus.Current.Equals(SystemStatus.Stopping) || 
                    SystemStatus.Current.Equals(SystemStatus.ShuttingDown) || 
                    SystemStatus.Current.Equals(SystemStatus.Terminated))
                {
                    stopAlreadyInProgress = true;
                    // Drop through to wait below
                }
                else if (!SystemStatus.Current.Equals(SystemStatus.Running))
                {
                    throw new InvalidOperationException(String.Format("Calling Silo.{0} on a silo which is not in the Running state. This silo is in the {1} state.", operation, SystemStatus.Current));
                }
                else
                {
                    if (gracefully)
                        SystemStatus.Current = SystemStatus.ShuttingDown;
                    else
                        SystemStatus.Current = SystemStatus.Stopping;
                }
            }

            if (stopAlreadyInProgress)
            {
                logger.Info(ErrorCode.SiloStopInProgress, "Silo termination is in progress - Will wait for it to finish");
                var pause = TimeSpan.FromSeconds(1);
                while (!SystemStatus.Current.Equals(SystemStatus.Terminated))
                {
                    logger.Info(ErrorCode.WaitingForSiloStop, "Waiting {0} for termination to complete", pause);
                    Thread.Sleep(pause);
                }
                return;
            }

            try
            {
                try
                {
                    if (gracefully)
                    {
                        logger.Info(ErrorCode.SiloShuttingDown, "Silo starting to Shutdown()");
                        // 1: Write "ShutDown" state in the table + broadcast gossip msgs to re-read the table to everyone
                        scheduler.QueueTask(LocalSiloStatusOracle.ShutDown, ((SystemTarget)LocalSiloStatusOracle).SchedulingContext)
                            .WaitWithThrow(stopTimeout);
                    }
                    else
                    {
                        logger.Info(ErrorCode.SiloStopping, "Silo starting to Stop()");
                        // 1: Write "Stopping" state in the table + broadcast gossip msgs to re-read the table to everyone
                        scheduler.QueueTask(LocalSiloStatusOracle.Stop, ((SystemTarget)LocalSiloStatusOracle).SchedulingContext)
                            .WaitWithThrow(stopTimeout);
                    }
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.SiloFailedToStopMembership, String.Format("Failed to {0} LocalSiloStatusOracle. About to FastKill this silo.", operation), exc);
                    return; // will go to finally
                }

                if (reminderService != null)
                {
                    // 2: Stop reminder service
                    scheduler.QueueTask(reminderService.Stop, ((SystemTarget) reminderService).SchedulingContext)
                        .WaitWithThrow(stopTimeout);
                }

                if (gracefully)
                {
                    // 3: Deactivate all grains
                    SafeExecute(() => catalog.DeactivateAllActivations().WaitWithThrow(stopTimeout));
                }


                // 3: Stop the gateway
                SafeExecute(messageCenter.StopAcceptingClientMessages);

                // 4: Start rejecting all silo to silo application messages
                SafeExecute(messageCenter.BlockApplicationMessages);

                // 5: Stop scheduling/executing application turns
                SafeExecute(scheduler.StopApplicationTurns);

                // 6: Directory: Speed up directory handoff
                // will be started automatically when directory receives SiloStatusChangeNotification(Stopping)

                // 7:
                SafeExecute(() => LocalGrainDirectory.StopPreparationCompletion.WaitWithThrow(stopTimeout));

                // The order of closing providers might be importan: Stats, streams, boostrap, storage.
                // Stats first since no one depends on it.
                // Storage should definitely be last since other providers ma ybe using it, potentilay indirectly.
                // Streams and Bootstrap - the order is less clear. Seems like Bootstrap may indirecly depend on Streams, but not the other way around.
                // 8:
                SafeExecute(() =>
                {
                    scheduler.QueueTask(() => statisticsProviderManager.CloseProviders(), providerManagerSystemTarget.SchedulingContext)
                            .WaitWithThrow(initTimeout);
                });
                // 9:
                SafeExecute(() =>
                {                
                    var siloStreamProviderManager = (StreamProviderManager)grainRuntime.StreamProviderManager;
                    scheduler.QueueTask(() => siloStreamProviderManager.CloseProviders(), providerManagerSystemTarget.SchedulingContext)
                            .WaitWithThrow(initTimeout);
                });
                // 10:
                SafeExecute(() =>
                {
                    scheduler.QueueTask(() => bootstrapProviderManager.CloseProviders(), providerManagerSystemTarget.SchedulingContext)
                            .WaitWithThrow(initTimeout);
                });
                // 11:
                SafeExecute(() =>
                {
                    scheduler.QueueTask(() => storageProviderManager.CloseProviders(), providerManagerSystemTarget.SchedulingContext)
                            .WaitWithThrow(initTimeout);
                });
            }
            finally
            {
                // 10, 11, 12: Write Dead in the table, Drain scheduler, Stop msg center, ...
                FastKill();
                logger.Info(ErrorCode.SiloStopped, "Silo is Stopped()");
            }
        }

        /// <summary>
        /// Ungracefully stop the run time system and the application running on it. 
        /// Applications requests would be abruptly terminated, and the internal system state quickly stopped with minimal cleanup.
        /// </summary>
        private void FastKill()
        {
            if (!GlobalConfig.LivenessType.Equals(GlobalConfiguration.LivenessProviderType.MembershipTableGrain))
            {
                // do not execute KillMyself if using MembershipTableGrain, since it will fail, as we've already stopped app scheduler turns.
                SafeExecute(() => scheduler.QueueTask( LocalSiloStatusOracle.KillMyself, ((SystemTarget)LocalSiloStatusOracle).SchedulingContext)
                    .WaitWithThrow(stopTimeout));
            }

            // incoming messages
            SafeExecute(incomingSystemAgent.Stop);
            SafeExecute(incomingPingAgent.Stop);
            SafeExecute(incomingAgent.Stop);

            // timers
            SafeExecute(InsideRuntimeClient.Current.Stop);
            if (platformWatchdog != null) 
                SafeExecute(platformWatchdog.Stop); // Silo may be dying before platformWatchdog was set up

            SafeExecute(scheduler.Stop);
            SafeExecute(scheduler.PrintStatistics);
            SafeExecute(activationDirectory.PrintActivationDirectory);
            SafeExecute(messageCenter.Stop);
            SafeExecute(siloStatistics.Stop);
            SafeExecute(TraceLogger.Close);

            SafeExecute(GrainTypeManager.Stop);

            UnobservedExceptionsHandlerClass.ResetUnobservedExceptionHandler();

            SystemStatus.Current = SystemStatus.Terminated;
            siloTerminatedEvent.Set();
        }

        private void SafeExecute(Action action)
        {
            Utils.SafeExecute(action, logger, "Silo.Stop");
        }

        private void HandleProcessExit(object sender, EventArgs e)
        {
            // NOTE: We need to minimize the amount of processing occurring on this code path -- we only have under approx 2-3 seconds before process exit will occur
            logger.Warn(ErrorCode.Runtime_Error_100220, "Process is exiting");
            TraceLogger.Flush();

            try
            {
                lock (lockable)
                {
                    if (!SystemStatus.Current.Equals(SystemStatus.Running)) return;
                    
                    SystemStatus.Current = SystemStatus.Stopping;
                }

                if (!TestHook.ExecuteFastKillInProcessExit) return;

                logger.Info(ErrorCode.SiloStopping, "Silo.HandleProcessExit() - starting to FastKill()");
                FastKill();
            }
            finally
            {
                TraceLogger.Close();
            }
        }

        /// <summary>
        /// Test hook functions for white box testing.
        /// </summary>
        public class TestHooks : MarshalByRefObject
        {
            private readonly Silo silo;
            internal bool ExecuteFastKillInProcessExit;
            
            internal IConsistentRingProvider ConsistentRingProvider
            {
                get { return CheckReturnBoundaryReference("ring provider", silo.RingProvider); }
            }
            
            public bool HasStatisticsProvider { get { return silo.statisticsProviderManager != null; } }

            public object StatisticsProvider
            {
                get
                {
                    if (silo.statisticsProviderManager == null) return null;
                    var provider = silo.statisticsProviderManager.GetProvider(silo.LocalConfig.StatisticsProviderName);
                    return CheckReturnBoundaryReference("statistics provider", provider);
                }
            }

            /// <summary>
            /// Populates the provided <paramref name="collection"/> with the assemblies generated by this silo.
            /// </summary>
            /// <param name="collection">The collection to populate.</param>
            public void UpdateGeneratedAssemblies(GeneratedAssemblies collection)
            {
                var generatedAssemblies = CodeGeneratorManager.GetGeneratedAssemblies();
                foreach (var asm in generatedAssemblies)
                {
                    collection.Add(asm.Key, asm.Value);
                }
            }

            internal Action<GrainId> Debug_OnDecideToCollectActivation { get; set; }

            internal TestHooks(Silo s)
            {
                silo = s;
                ExecuteFastKillInProcessExit = true;
            }

            internal Guid ServiceId { get { return silo.GlobalConfig.ServiceId; } }

            /// <summary>
            /// Get list of providers loaded in this silo.
            /// </summary>
            /// <returns></returns>
            internal IEnumerable<string> GetStorageProviderNames()
            {
                return silo.StorageProviderManager.GetProviderNames();
            }

            /// <summary>
            /// Find the named storage provider loaded in this silo.
            /// </summary>
            /// <returns></returns>
            internal IStorageProvider GetStorageProvider(string name)
            {
                var provider = silo.StorageProviderManager.GetProvider(name) as IStorageProvider;
                return CheckReturnBoundaryReference("storage provider", provider);
            }

            internal string PrintSiloConfig()
            {
                return silo.OrleansConfig.ToString(silo.Name);
            }

            internal IBootstrapProvider GetBootstrapProvider(string name)
            {
                var provider = silo.BootstrapProviders.First(p => p.Name == name);
                return CheckReturnBoundaryReference("bootstrap provider", provider);
            }

            internal void SuppressFastKillInHandleProcessExit()
            {
                ExecuteFastKillInProcessExit = false;
            }

            // store silos for which we simulate faulty communication
            // number indicates how many percent of requests are lost
            internal ConcurrentDictionary<IPEndPoint, double> SimulatedMessageLoss; 

            internal void BlockSiloCommunication(IPEndPoint destination, double lost_percentage)
            {
                if (SimulatedMessageLoss == null)
                    SimulatedMessageLoss = new ConcurrentDictionary<IPEndPoint, double>();

                SimulatedMessageLoss[destination] = lost_percentage;
            }

            internal void UnblockSiloCommunication()
            {
                SimulatedMessageLoss = null;
            }

            SafeRandom random = new SafeRandom();

            internal bool ShouldDrop(Message msg)
            {
                if (SimulatedMessageLoss != null)
                {
                    double blockedpercentage = 0.0;
                    Silo.CurrentSilo.TestHook.SimulatedMessageLoss.TryGetValue(msg.TargetSilo.Endpoint, out blockedpercentage);
                    return (random.NextDouble() * 100 < blockedpercentage);
                }
                else
                    return false;
            }

            // this is only for white box testing - use RuntimeClient.Current.SendRequest instead

            internal void SendMessageInternal(Message message)
            {
                silo.messageCenter.SendMessage(message);
            }

            // For white-box testing only

            internal int UnregisterGrainForTesting(GrainId grain)
            {
                return silo.catalog.UnregisterGrainForTesting(grain);
            }

            // For white-box testing only

            internal void SetDirectoryLazyDeregistrationDelay_ForTesting(TimeSpan timeSpan)
            {
                silo.OrleansConfig.Globals.DirectoryLazyDeregistrationDelay = timeSpan;
            }

            // For white-box testing only

            internal void SetMaxForwardCount_ForTesting(int val)
            {
                silo.OrleansConfig.Globals.MaxForwardCount = val;
            }

            private static T CheckReturnBoundaryReference<T>(string what, T obj) where T : class
            {
                if (obj == null) return null;
                if (obj is MarshalByRefObject || obj is ISerializable)
                {
                    // Referernce to the provider can safely be passed across app-domain boundary in unit test process
                    return obj;
                }
                throw new InvalidOperationException(string.Format("Cannot return reference to {0} {1} if it is not MarshalByRefObject or Serializable",
                    what, TypeUtils.GetFullName(obj.GetType())));
            }

            /// <summary>
            /// Represents a collection of generated assemblies accross an application domain.
            /// </summary>
            public class GeneratedAssemblies : MarshalByRefObject
            {
                /// <summary>
                /// Initializes a new instance of the <see cref="GeneratedAssemblies"/> class.
                /// </summary>
                public GeneratedAssemblies()
                {
                    this.Assemblies = new Dictionary<string, byte[]>();
                }

                /// <summary>
                /// Gets the assemblies which were produced by code generation.
                /// </summary>
                public Dictionary<string, byte[]> Assemblies { get; private set; }

                /// <summary>
                /// Adds a new assembly to this collection.
                /// </summary>
                /// <param name="key">
                /// The full name of the assembly which code was generated for.
                /// </param>
                /// <param name="value">
                /// The raw generated assembly.
                /// </param>
                public void Add(string key, byte[] value)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        this.Assemblies[key] = value;
                    }
                }
            }

            /// <summary>
            /// Methods for optimizing the code generator.
            /// </summary>
            public class CodeGeneratorOptimizer : MarshalByRefObject
            {
                /// <summary>
                /// Adds a cached assembly to the code generator.
                /// </summary>
                /// <param name="targetAssemblyName">The assembly which the cached assembly was generated for.</param>
                /// <param name="cachedAssembly">The generated assembly.</param>
                public void AddCachedAssembly(string targetAssemblyName, byte[] cachedAssembly)
                {
                    CodeGeneratorManager.AddGeneratedAssembly(targetAssemblyName, cachedAssembly);
                }
            }
        }

        private void UnobservedExceptionHandler(ISchedulingContext context, Exception exception)
        {
            var schedulingContext = context as SchedulingContext;
            if (schedulingContext == null)
            {
                if (context == null)
                    logger.Error(ErrorCode.Runtime_Error_100102, String.Format("Silo caught an UnobservedException with context==null."), exception);
                else
                    logger.Error(ErrorCode.Runtime_Error_100103, String.Format("Silo caught an UnobservedException with context of type different than OrleansContext. The type of the context is {0}. The context is {1}",
                        context.GetType(), context), exception);
            }
            else
            {
                logger.Error(ErrorCode.Runtime_Error_100104, String.Format("Silo caught an UnobservedException thrown by {0}.", schedulingContext.Activation), exception);
            }   
        }

        private void DomainUnobservedExceptionHandler(object context, Exception exception)
        {
            if (context is ISchedulingContext)
                UnobservedExceptionHandler(context as ISchedulingContext, exception);
            else
                logger.Error(ErrorCode.Runtime_Error_100324, String.Format("Called DomainUnobservedExceptionHandler with context {0}.", context), exception);
        }

        internal void RegisterSystemTarget(SystemTarget target)
        {
            scheduler.RegisterWorkContext(target.SchedulingContext);
            activationDirectory.RecordNewSystemTarget(target);
        }

        internal void UnregisterSystemTarget(SystemTarget target)
        {
            activationDirectory.RemoveSystemTarget(target);
            scheduler.UnregisterWorkContext(target.SchedulingContext);
        }

        /// <summary> Return dump of diagnostic data from this silo. </summary>
        /// <param name="all"></param>
        /// <returns>Debug data for this silo.</returns>
        public string GetDebugDump(bool all = true)
        {
            var sb = new StringBuilder();            
            foreach (var sytemTarget in activationDirectory.AllSystemTargets())
                sb.AppendFormat("System target {0}:", sytemTarget.GrainId.ToString()).AppendLine();               
            
            var enumerator = activationDirectory.GetEnumerator();
            while(enumerator.MoveNext())
            {
                Utils.SafeExecute(() =>
                {
                    var activationData = enumerator.Current.Value;
                    var workItemGroup = scheduler.GetWorkItemGroup(new SchedulingContext(activationData));
                    if (workItemGroup == null)
                    {
                        sb.AppendFormat("Activation with no work item group!! Grain {0}, activation {1}.",
                            activationData.Grain,
                            activationData.ActivationId);
                        sb.AppendLine();
                        return;
                    }

                    if (all || activationData.State.Equals(ActivationState.Valid))
                    {
                        sb.AppendLine(workItemGroup.DumpStatus());
                        sb.AppendLine(activationData.DumpStatus());
                    }
                });
            }
            logger.Info(ErrorCode.SiloDebugDump, sb.ToString());
            return sb.ToString();
        }

        /// <summary> Object.ToString override -- summary info for this silo. </summary>
        public override string ToString()
        {
            return localGrainDirectory.ToString();
        }
    }

    // A dummy system target to use for scheduling context for provider Init calls, to allow them to make grain calls
    internal class ProviderManagerSystemTarget : SystemTarget
    {
        public ProviderManagerSystemTarget(Silo currentSilo)
            : base(Constants.ProviderManagerSystemTargetId, currentSilo.SiloAddress)
        {
        }
    }
}

