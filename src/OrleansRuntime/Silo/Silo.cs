using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.GrainDirectory;
using Orleans.LogConsistency;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.LogConsistency;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Providers;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Startup;
using Orleans.Runtime.Storage;
using Orleans.Runtime.TestHooks;
using Orleans.Serialization;
using Orleans.Services;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.Timers;
using Orleans.MultiCluster;

namespace Orleans.Runtime
{
    /// <summary>
    /// Orleans silo.
    /// </summary>
    public class Silo
    {
        /// <summary> Standard name for Primary silo. </summary>
        public const string PrimarySiloName = "Primary";

        /// <summary> Silo Types. </summary>
        public enum SiloType
        {
            /// <summary> No silo type specified. </summary>
            None = 0,
            /// <summary> Primary silo. </summary>
            Primary,
            /// <summary> Secondary silo. </summary>
            Secondary,
        }
        
        private readonly SiloInitializationParameters initializationParams;
        private readonly ISiloMessageCenter messageCenter;
        private readonly OrleansTaskScheduler scheduler;
        private readonly LocalGrainDirectory localGrainDirectory;
        private readonly ActivationDirectory activationDirectory;
        private readonly IncomingMessageAgent incomingAgent;
        private readonly IncomingMessageAgent incomingSystemAgent;
        private readonly IncomingMessageAgent incomingPingAgent;
        private readonly Logger logger;
        private readonly GrainTypeManager grainTypeManager;
        private TypeManager typeManager;
        private readonly ManualResetEvent siloTerminatedEvent;
        private readonly SiloStatisticsManager siloStatistics;
        private readonly InsideRuntimeClient runtimeClient;
        private readonly AssemblyProcessor assemblyProcessor;
        private StorageProviderManager storageProviderManager;
        private LogConsistencyProviderManager logConsistencyProviderManager;
        private StatisticsProviderManager statisticsProviderManager;
        private BootstrapProviderManager bootstrapProviderManager;
        private IReminderService reminderService;
        private ProviderManagerSystemTarget providerManagerSystemTarget;
        private readonly IMembershipOracle membershipOracle;
        private readonly IMultiClusterOracle multiClusterOracle;

        private Watchdog platformWatchdog;
        private readonly TimeSpan initTimeout;
        private readonly TimeSpan stopTimeout = TimeSpan.FromMinutes(1);
        private readonly Catalog catalog;
        private readonly List<IHealthCheckParticipant> healthCheckParticipants = new List<IHealthCheckParticipant>();
        private readonly object lockable = new object();
        private readonly GrainFactory grainFactory;
        private readonly IGrainRuntime grainRuntime;
        private readonly List<IProvider> allSiloProviders = new List<IProvider>();
        
        /// <summary>
        /// Gets the type of this 
        /// </summary>
        public SiloType Type => this.initializationParams.Type;
        internal string Name => this.initializationParams.Name;
        internal ClusterConfiguration OrleansConfig => this.initializationParams.ClusterConfig;
        internal GlobalConfiguration GlobalConfig => this.initializationParams.GlobalConfig;
        internal NodeConfiguration LocalConfig => this.initializationParams.NodeConfig;
        internal OrleansTaskScheduler LocalScheduler { get { return scheduler; } }
        internal GrainTypeManager LocalGrainTypeManager { get { return grainTypeManager; } }
        internal ILocalGrainDirectory LocalGrainDirectory { get { return localGrainDirectory; } }
        internal IMultiClusterOracle LocalMultiClusterOracle { get { return multiClusterOracle; } }
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal ILogConsistencyProviderManager LogConsistencyProviderManager { get { return logConsistencyProviderManager; } }
        internal IStorageProviderManager StorageProviderManager { get { return storageProviderManager; } }
        internal IProviderManager StatisticsProviderManager { get { return statisticsProviderManager; } }
        internal IStreamProviderManager StreamProviderManager { get { return grainRuntime.StreamProviderManager; } }
        internal IList<IBootstrapProvider> BootstrapProviders { get; private set; }
        internal ISiloPerformanceMetrics Metrics { get { return siloStatistics.MetricsTable; } }
        internal static Silo CurrentSilo { get; private set; }
        internal IReadOnlyCollection<IProvider> AllSiloProviders 
        {
            get { return allSiloProviders.AsReadOnly();  }
        }
        internal ICatalog Catalog => catalog;

        internal IServiceProvider Services { get; }


        /// <summary> Gets whether this cluster is configured to be part of a multicluster. </summary>
        public bool HasMultiClusterNetwork
        {
            get { return GlobalConfig.HasMultiClusterNetwork; }
        }

        /// <summary> Get the id of the cluster this silo is part of. </summary>
        public string ClusterId
        {
            get {
                var configuredId = GlobalConfig.HasMultiClusterNetwork ? GlobalConfig.ClusterId : GlobalConfig.DeploymentId;
                return string.IsNullOrEmpty(configuredId) ? CLUSTER_ID_DEFAULT : configuredId; 
            } 
        }

        private const string CLUSTER_ID_DEFAULT = "DefaultClusterID"; // if no id is configured, we pick a nonempty default.

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => this.initializationParams.SiloAddress;

        /// <summary>
        ///  Silo termination event used to signal shutdown of this silo.
        /// </summary>
        public WaitHandle SiloTerminatedEvent { get { return siloTerminatedEvent; } } // one event for all types of termination (shutdown, stop and fast kill).

        /// <summary>
        /// Test hook connection for white-box testing of silo.
        /// </summary>
        internal TestHooksSystemTarget testHook;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="Silo"/> class.
        /// </summary>
        /// <param name="name">Name of this silo.</param>
        /// <param name="siloType">Type of this silo.</param>
        /// <param name="config">Silo config data to be used for this silo.</param>
        public Silo(string name, SiloType siloType, ClusterConfiguration config)
            : this(new SiloInitializationParameters(name, siloType, config))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Silo"/> class.
        /// </summary>
        /// <param name="initializationParams">
        /// The silo initialization parameters.
        /// </param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Should not Dispose of messageCenter in this method because it continues to run / exist after this point.")]
        internal Silo(SiloInitializationParameters initializationParams)
        {
            string name = initializationParams.Name;
            ClusterConfiguration config = initializationParams.ClusterConfig;
            this.initializationParams = initializationParams;

            SystemStatus.Current = SystemStatus.Creating;

            CurrentSilo = this;

            var startTime = DateTime.UtcNow;
            
            siloTerminatedEvent = new ManualResetEvent(false);
            
            if (!LogManager.IsInitialized)
                LogManager.Initialize(LocalConfig);

            config.OnConfigChange("Defaults/Tracing", () => LogManager.Initialize(LocalConfig, true), false);
            MultiClusterRegistrationStrategy.Initialize(config.Globals);
            StatisticsCollector.Initialize(LocalConfig);
            
            SerializationManager.Initialize(GlobalConfig.SerializationProviders, this.GlobalConfig.FallbackSerializationProvider);
            initTimeout = GlobalConfig.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), GlobalConfig.MaxJoinAttemptTime);
                stopTimeout = initTimeout;
            }

            var localEndpoint = this.initializationParams.SiloAddress.Endpoint;
            LogManager.MyIPEndPoint = localEndpoint;
            logger = LogManager.GetLogger("Silo", LoggerType.Runtime);

            logger.Info(ErrorCode.SiloGcSetting, "Silo starting with GC settings: ServerGC={0} GCLatencyMode={1}", GCSettings.IsServerGC, Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode));
            if (!GCSettings.IsServerGC || !GCSettings.LatencyMode.Equals(GCLatencyMode.Batch))
            {
                logger.Warn(ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on or with GCLatencyMode.Batch enabled - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\"> and <configuration>-<runtime>-<gcConcurrent enabled=\"false\"/>");
                logger.Warn(ErrorCode.SiloGcWarning, "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines).");
            }

            logger.Info(ErrorCode.SiloInitializing, "-------------- Initializing {0} silo on host {1} MachineName {2} at {3}, gen {4} --------------",
                this.initializationParams.Type, LocalConfig.DNSHostName, Environment.MachineName, localEndpoint, this.initializationParams.SiloAddress.Generation);
            logger.Info(ErrorCode.SiloInitConfig, "Starting silo {0} with the following configuration= " + Environment.NewLine + "{1}",
                name, config.ToString(name));

            // Register system services.
            var services = new ServiceCollection();
            services.AddSingleton(this);
            services.AddSingleton(initializationParams);
            services.AddSingleton<ILocalSiloDetails>(initializationParams);
            services.AddSingleton(initializationParams.ClusterConfig);
            services.AddSingleton(initializationParams.GlobalConfig);
            services.AddTransient(sp => initializationParams.NodeConfig);
            services.AddSingleton<ITimerRegistry, TimerRegistry>();
            services.AddSingleton<IReminderRegistry, ReminderRegistry>();
            services.AddSingleton<IStreamProviderManager, StreamProviderManager>();
            services.AddSingleton<GrainRuntime>();
            services.AddSingleton<IGrainRuntime, GrainRuntime>();
            services.AddSingleton<OrleansTaskScheduler>();
            services.AddSingleton<GrainFactory>(sp => sp.GetService<InsideRuntimeClient>().ConcreteGrainFactory);
            services.AddFromExisting<IGrainFactory, GrainFactory>();
            services.AddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.AddSingleton<TypeMetadataCache>();
            services.AddSingleton<AssemblyProcessor>();
            services.AddSingleton<ActivationDirectory>();
            services.AddSingleton<LocalGrainDirectory>();
            services.AddFromExisting<ILocalGrainDirectory, LocalGrainDirectory>();
            services.AddSingleton<SiloStatisticsManager>();
            services.AddSingleton<ISiloPerformanceMetrics>(sp => sp.GetRequiredService<SiloStatisticsManager>().MetricsTable);
            services.AddSingleton<SiloAssemblyLoader>();
            services.AddSingleton<GrainTypeManager>();
            services.AddFromExisting<IMessagingConfiguration, GlobalConfiguration>();
            services.AddSingleton<MessageCenter>();
            services.AddFromExisting<IMessageCenter, MessageCenter>();
            services.AddFromExisting<ISiloMessageCenter, MessageCenter>();
            services.AddSingleton<Dispatcher>(sp => sp.GetRequiredService<Catalog>().Dispatcher);
            services.AddSingleton<InsideRuntimeClient>();
            services.AddFromExisting<IRuntimeClient, InsideRuntimeClient>();
            services.AddFromExisting<ISiloRuntimeClient, InsideRuntimeClient>();
            services.AddSingleton<MultiClusterGossipChannelFactory>();
            services.AddSingleton<MultiClusterOracle>();
            services.AddFromExisting<IMultiClusterOracle, MultiClusterOracle>();
            services.AddSingleton<DeploymentLoadPublisher>();
            services.AddSingleton<MembershipOracle>();
            services.AddFromExisting<IMembershipOracle, MembershipOracle>();
            services.AddFromExisting<ISiloStatusOracle, MembershipOracle>();
            services.AddSingleton<MembershipTableFactory>();
            services.AddSingleton<LocalReminderServiceFactory>();
            services.AddSingleton<ClientObserverRegistrar>();
            services.AddSingleton<SiloProviderRuntime>();
            services.AddFromExisting<IStreamProviderRuntime, SiloProviderRuntime>();
            services.AddSingleton<ImplicitStreamSubscriberTable>();
            services.AddSingleton<Func<string, Logger>>(LogManager.GetLogger);

            // Placement
            services.AddSingleton<PlacementDirectorsManager>();
            services.AddSingleton<IPlacementDirector<RandomPlacement>, RandomPlacementDirector>();
            services.AddSingleton<IPlacementDirector<PreferLocalPlacement>, PreferLocalPlacementDirector>();
            services.AddSingleton<IPlacementDirector<StatelessWorkerPlacement>, StatelessWorkerDirector>();
            services.AddSingleton<IPlacementDirector<ActivationCountBasedPlacement>, ActivationCountPlacementDirector>();
            services.AddSingleton<DefaultPlacementStrategy>();
            services.AddSingleton<ClientObserversPlacementDirector>();

            services.AddSingleton<Func<IGrainRuntime>>(sp => () => sp.GetRequiredService<IGrainRuntime>());

            // Grain activation
            services.AddSingleton<Catalog>();
            services.AddSingleton<GrainCreator>();
            services.AddSingleton<IGrainActivator, DefaultGrainActivator>();

            if (initializationParams.GlobalConfig.UseVirtualBucketsConsistentRing)
            {
                services.AddSingleton<IConsistentRingProvider>(
                    sp =>
                    new VirtualBucketsRingProvider(
                        this.initializationParams.SiloAddress,
                        this.initializationParams.GlobalConfig.NumVirtualBucketsConsistentRing));
            }
            else
            {
                services.AddSingleton<IConsistentRingProvider>(
                    sp => new ConsistentRingProvider(this.initializationParams.SiloAddress));
            }

            // Configure DI using Startup type
            this.Services = StartupBuilder.ConfigureStartup(this.LocalConfig.StartupTypeName, services);

            this.assemblyProcessor = this.Services.GetRequiredService<AssemblyProcessor>();
            this.assemblyProcessor.Initialize();

            BufferPool.InitGlobalBufferPool(GlobalConfig);

            UnobservedExceptionsHandlerClass.SetUnobservedExceptionHandler(UnobservedExceptionHandler);
            AppDomain.CurrentDomain.UnhandledException += this.DomainUnobservedExceptionHandler;

            try
            {
                grainFactory = Services.GetRequiredService<GrainFactory>();
            }
            catch (InvalidOperationException exc)
            {
                logger.Error(ErrorCode.SiloStartError, "Exception during Silo.Start, GrainFactory was not registered in Dependency Injection container", exc);
                throw;
            }

            grainTypeManager = Services.GetRequiredService<GrainTypeManager>();

            // Performance metrics
            siloStatistics = Services.GetRequiredService<SiloStatisticsManager>();

            // The scheduler
            scheduler = Services.GetRequiredService<OrleansTaskScheduler>();
            healthCheckParticipants.Add(scheduler);
            
            runtimeClient = Services.GetRequiredService<InsideRuntimeClient>();

            // Initialize the message center
            messageCenter = Services.GetRequiredService<MessageCenter>();
            messageCenter.RerouteHandler = runtimeClient.RerouteMessage;
            messageCenter.SniffIncomingMessage = runtimeClient.SniffIncomingMessage;

            // GrainRuntime can be created only here, after messageCenter was created.
            grainRuntime = Services.GetRequiredService<IGrainRuntime>();

            // Now the router/directory service
            // This has to come after the message center //; note that it then gets injected back into the message center.;
            localGrainDirectory = Services.GetRequiredService<LocalGrainDirectory>(); 
            
            // Now the activation directory.
            activationDirectory = Services.GetRequiredService<ActivationDirectory>();
            
            // Now the consistent ring provider
            RingProvider = Services.GetRequiredService<IConsistentRingProvider>();

            // to preserve backwards compatibility, only use the service provider to inject grain dependencies if the user supplied his own
            // service provider, meaning that he is explicitly opting into it.
            catalog = Services.GetRequiredService<Catalog>();

            siloStatistics.MetricsTable.Scheduler = scheduler;
            siloStatistics.MetricsTable.ActivationDirectory = activationDirectory;
            siloStatistics.MetricsTable.ActivationCollector = catalog.ActivationCollector;
            siloStatistics.MetricsTable.MessageCenter = messageCenter;
            
            // Now the incoming message agents
            incomingSystemAgent = new IncomingMessageAgent(Message.Categories.System, messageCenter, activationDirectory, scheduler, catalog.Dispatcher);
            incomingPingAgent = new IncomingMessageAgent(Message.Categories.Ping, messageCenter, activationDirectory, scheduler, catalog.Dispatcher);
            incomingAgent = new IncomingMessageAgent(Message.Categories.Application, messageCenter, activationDirectory, scheduler, catalog.Dispatcher);
            
            membershipOracle = Services.GetRequiredService<IMembershipOracle>();

            if (!this.GlobalConfig.HasMultiClusterNetwork)
            {
                logger.Info("Skip multicluster oracle creation (no multicluster network configured)");
            }
            else
            {
                multiClusterOracle = Services.GetRequiredService<IMultiClusterOracle>();
            }

            SystemStatus.Current = SystemStatus.Created;

            StringValueStatistic.FindOrCreate(StatisticNames.SILO_START_TIME,
                () => LogFormatter.PrintDate(startTime)); // this will help troubleshoot production deployment when looking at MDS logs.

            logger.Info(ErrorCode.SiloInitializingFinished, "-------------- Started silo {0}, ConsistentHashCode {1:X} --------------", SiloAddress.ToLongString(), SiloAddress.GetConsistentHashCode());
        }

        private void CreateSystemTargets()
        {
            logger.Verbose("Creating System Targets for this silo.");

            logger.Verbose("Creating {0} System Target", "SiloControl");
            var siloControl = ActivatorUtilities.CreateInstance<SiloControl>(Services);
            RegisterSystemTarget(siloControl);

            logger.Verbose("Creating {0} System Target", "StreamProviderUpdateAgent");
            RegisterSystemTarget(
                new StreamProviderManagerAgent(this, allSiloProviders, Services.GetRequiredService<IStreamProviderRuntime>()));

            logger.Verbose("Creating {0} System Target", "ProtocolGateway");
            RegisterSystemTarget(new ProtocolGateway(this.SiloAddress));

            logger.Verbose("Creating {0} System Target", "DeploymentLoadPublisher");
            RegisterSystemTarget(Services.GetRequiredService<DeploymentLoadPublisher>());
            
            logger.Verbose("Creating {0} System Target", "RemoteGrainDirectory + CacheValidator");
            RegisterSystemTarget(LocalGrainDirectory.RemoteGrainDirectory);
            RegisterSystemTarget(LocalGrainDirectory.CacheValidator);

            logger.Verbose("Creating {0} System Target", "RemoteClusterGrainDirectory");
            RegisterSystemTarget(LocalGrainDirectory.RemoteClusterGrainDirectory);

            logger.Verbose("Creating {0} System Target", "ClientObserverRegistrar + TypeManager");

            this.RegisterSystemTarget(this.Services.GetRequiredService<ClientObserverRegistrar>());
            var implicitStreamSubscriberTable = Services.GetRequiredService<ImplicitStreamSubscriberTable>();
            typeManager = new TypeManager(SiloAddress, this.grainTypeManager, membershipOracle, LocalScheduler, GlobalConfig.TypeMapRefreshInterval, implicitStreamSubscriberTable);
            this.RegisterSystemTarget(typeManager);

            logger.Verbose("Creating {0} System Target", "MembershipOracle");
            if (this.membershipOracle is SystemTarget)
            {
                RegisterSystemTarget((SystemTarget)membershipOracle);
            }

            if (multiClusterOracle != null && multiClusterOracle is SystemTarget)
            {
                logger.Verbose("Creating {0} System Target", "MultiClusterOracle");
                RegisterSystemTarget((SystemTarget)multiClusterOracle);
            }

            logger.Verbose("Finished creating System Targets for this silo.");
        }

        internal void InitializeTestHooksSystemTarget()
        {
            testHook = new TestHooksSystemTarget(this);
            RegisterSystemTarget(testHook);
        }

        private void InjectDependencies()
        {
            healthCheckParticipants.Add(membershipOracle);

            catalog.SiloStatusOracle = this.membershipOracle;
            localGrainDirectory.CatalogSiloStatusListener = catalog;
            this.membershipOracle.SubscribeToSiloStatusEvents(localGrainDirectory);
            messageCenter.SiloDeadOracle = this.membershipOracle.IsDeadSilo;

            // consistentRingProvider is not a system target per say, but it behaves like the localGrainDirectory, so it is here
            this.membershipOracle.SubscribeToSiloStatusEvents((ISiloStatusListener)RingProvider);

            this.membershipOracle.SubscribeToSiloStatusEvents(typeManager);

            this.membershipOracle.SubscribeToSiloStatusEvents(Services.GetRequiredService<DeploymentLoadPublisher>());

            this.membershipOracle.SubscribeToSiloStatusEvents(Services.GetRequiredService<ClientObserverRegistrar>());

            if (!GlobalConfig.ReminderServiceType.Equals(GlobalConfiguration.ReminderServiceProviderType.Disabled))
            {
                // start the reminder service system target
                reminderService = Services.GetRequiredService<LocalReminderServiceFactory>()
                                          .CreateReminderService(this, grainFactory, initTimeout, this.runtimeClient);
                var reminderServiceSystemTarget = this.reminderService as SystemTarget;
                if (reminderServiceSystemTarget != null) RegisterSystemTarget(reminderServiceSystemTarget);
            }

            RegisterSystemTarget(catalog);
            scheduler.QueueAction(catalog.Start, catalog.SchedulingContext)
                .WaitWithThrow(initTimeout);

            // SystemTarget for provider init calls
            providerManagerSystemTarget = new ProviderManagerSystemTarget(this);
            RegisterSystemTarget(providerManagerSystemTarget);
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
            grainTypeManager.Start();
            runtimeClient.Start();

            // The order of these 4 is pretty much arbitrary.
            scheduler.Start();
            messageCenter.Start();
            incomingPingAgent.Start();
            incomingSystemAgent.Start();
            incomingAgent.Start();

            LocalGrainDirectory.Start();

            // Set up an execution context for this thread so that the target creation steps can use asynch values.
            RuntimeContext.InitializeMainThread();
            
            // Initialize the implicit stream subscribers table.
            var implicitStreamSubscriberTable = Services.GetRequiredService<ImplicitStreamSubscriberTable>();
            implicitStreamSubscriberTable.InitImplicitStreamSubscribers(this.grainTypeManager.GrainClassTypeData.Select(t => t.Value.Type).ToArray());

            var siloProviderRuntime = Services.GetRequiredService<SiloProviderRuntime>();
            runtimeClient.CurrentStreamProviderRuntime = siloProviderRuntime;
            statisticsProviderManager = new StatisticsProviderManager(ProviderCategoryConfiguration.STATISTICS_PROVIDER_CATEGORY_NAME, siloProviderRuntime);
            string statsProviderName =  statisticsProviderManager.LoadProvider(GlobalConfig.ProviderConfigurations)
                .WaitForResultWithThrow(initTimeout);
            if (statsProviderName != null)
                LocalConfig.StatisticsProviderName = statsProviderName;
            allSiloProviders.AddRange(statisticsProviderManager.GetProviders());

            // can call SetSiloMetricsTableDataManager only after MessageCenter is created (dependency on this.SiloAddress).
            siloStatistics.SetSiloStatsTableDataManager(this, LocalConfig).WaitWithThrow(initTimeout);
            siloStatistics.SetSiloMetricsTableDataManager(this, LocalConfig).WaitWithThrow(initTimeout);


            // This has to follow the above steps that start the runtime components
            CreateSystemTargets();

            InjectDependencies();

            // Validate the configuration.
            GlobalConfig.Application.ValidateConfiguration(logger);
            
            // Initialize storage providers once we have a basic silo runtime environment operating
            storageProviderManager = new StorageProviderManager(grainFactory, Services, siloProviderRuntime);
            scheduler.QueueTask(
                () => storageProviderManager.LoadStorageProviders(GlobalConfig.ProviderConfigurations),
                providerManagerSystemTarget.SchedulingContext)
                    .WaitWithThrow(initTimeout);
            catalog.SetStorageManager(storageProviderManager);
            allSiloProviders.AddRange(storageProviderManager.GetProviders());
            if (logger.IsVerbose) { logger.Verbose("Storage provider manager created successfully."); }

            // Initialize log consistency providers once we have a basic silo runtime environment operating
            logConsistencyProviderManager = new LogConsistencyProviderManager(grainFactory, Services, siloProviderRuntime);
            scheduler.QueueTask(
                () => logConsistencyProviderManager.LoadLogConsistencyProviders(GlobalConfig.ProviderConfigurations),
                providerManagerSystemTarget.SchedulingContext)
                    .WaitWithThrow(initTimeout);
            catalog.SetLogConsistencyManager(logConsistencyProviderManager);
            if (logger.IsVerbose) { logger.Verbose("Log consistency provider manager created successfully."); }

            // Load and init stream providers before silo becomes active
            var siloStreamProviderManager = (StreamProviderManager)grainRuntime.StreamProviderManager;
            scheduler.QueueTask(
                () => siloStreamProviderManager.LoadStreamProviders(GlobalConfig.ProviderConfigurations, siloProviderRuntime),
                    providerManagerSystemTarget.SchedulingContext)
                        .WaitWithThrow(initTimeout);
            runtimeClient.CurrentStreamProviderManager = siloStreamProviderManager;
            allSiloProviders.AddRange(siloStreamProviderManager.GetProviders());
            if (logger.IsVerbose) { logger.Verbose("Stream provider manager created successfully."); }

            // Load and init grain services before silo becomes active.
            CreateGrainServices(GlobalConfig.GrainServiceConfigurations);

            ISchedulingContext statusOracleContext = (this.membershipOracle as SystemTarget)?.SchedulingContext;
            scheduler.QueueTask(() => this.membershipOracle.Start(), statusOracleContext)
                .WaitWithThrow(initTimeout);
            if (logger.IsVerbose) { logger.Verbose("Local silo status oracle created successfully."); }
            scheduler.QueueTask(this.membershipOracle.BecomeActive, statusOracleContext)
                .WaitWithThrow(initTimeout);
            if (logger.IsVerbose) { logger.Verbose("Local silo status oracle became active successfully."); }

            //if running in multi cluster scenario, start the MultiClusterNetwork Oracle
            if (GlobalConfig.HasMultiClusterNetwork) 
            {
                logger.Info("Starting multicluster oracle with my ServiceId={0} and ClusterId={1}.",
                    GlobalConfig.ServiceId, GlobalConfig.ClusterId);

                ISchedulingContext clusterStatusContext = (multiClusterOracle as SystemTarget)?.SchedulingContext;
                scheduler.QueueTask(() => multiClusterOracle.Start(), clusterStatusContext)
                                    .WaitWithThrow(initTimeout);
                if (logger.IsVerbose) { logger.Verbose("multicluster oracle created successfully."); }
            }

            try
            {
                this.siloStatistics.Start(this.LocalConfig);
                if (this.logger.IsVerbose) { this.logger.Verbose("Silo statistics manager started successfully."); }

                // Finally, initialize the deployment load collector, for grains with load-based placement
                var deploymentLoadPublisher = Services.GetRequiredService<DeploymentLoadPublisher>();
                this.scheduler.QueueTask(deploymentLoadPublisher.Start, deploymentLoadPublisher.SchedulingContext)
                    .WaitWithThrow(this.initTimeout);
                if (this.logger.IsVerbose) { this.logger.Verbose("Silo deployment load publisher started successfully."); }

                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                this.platformWatchdog = new Watchdog(this.LocalConfig.StatisticsLogWriteInterval, this.healthCheckParticipants);
                this.platformWatchdog.Start();
                if (this.logger.IsVerbose) { this.logger.Verbose("Silo platform watchdog started successfully."); }

                if (this.reminderService != null)
                {
                    // so, we have the view of the membership in the consistentRingProvider. We can start the reminder service
                    this.scheduler.QueueTask(this.reminderService.Start, (this.reminderService as SystemTarget)?.SchedulingContext)
                        .WaitWithThrow(this.initTimeout);
                    if (this.logger.IsVerbose)
                    {
                        this.logger.Verbose("Reminder service started successfully.");
                    }
                }

                this.bootstrapProviderManager = new BootstrapProviderManager();
                this.scheduler.QueueTask(
                    () => this.bootstrapProviderManager.LoadAppBootstrapProviders(siloProviderRuntime, this.GlobalConfig.ProviderConfigurations),
                    this.providerManagerSystemTarget.SchedulingContext)
                        .WaitWithThrow(this.initTimeout);
                this.BootstrapProviders = this.bootstrapProviderManager.GetProviders(); // Data hook for testing & diagnotics
                this.allSiloProviders.AddRange(this.BootstrapProviders);

                if (this.logger.IsVerbose) { this.logger.Verbose("App bootstrap calls done successfully."); }

                // Start stream providers after silo is active (so the pulling agents don't start sending messages before silo is active).
                // also after bootstrap provider started so bootstrap provider can initialize everything stream before events from this silo arrive.
                this.scheduler.QueueTask(siloStreamProviderManager.StartStreamProviders, this.providerManagerSystemTarget.SchedulingContext)
                    .WaitWithThrow(this.initTimeout);
                if (this.logger.IsVerbose) { this.logger.Verbose("Stream providers started successfully."); }

                // Now that we're active, we can start the gateway
                var mc = this.messageCenter as MessageCenter;
                mc?.StartGateway(this.Services.GetRequiredService<ClientObserverRegistrar>());
                if (this.logger.IsVerbose) { this.logger.Verbose("Message gateway service started successfully."); }

                SystemStatus.Current = SystemStatus.Running;
            }
            catch (Exception exc)
            {
                this.SafeExecute(() => this.logger.Error(ErrorCode.Runtime_Error_100330, String.Format("Error starting silo {0}. Going to FastKill().", this.SiloAddress), exc));
                this.FastKill(); // if failed after Membership became active, mark itself as dead in Membership abale.
                throw;
            }
            if (logger.IsVerbose) { logger.Verbose("Silo.Start complete: System status = {0}", SystemStatus.Current); }
        }

        private void CreateGrainServices(GrainServiceConfigurations grainServiceConfigurations)
        {
            foreach (var serviceConfig in grainServiceConfigurations.GrainServices)
            {
                // Construct the Grain Service
                var serviceType = System.Type.GetType(serviceConfig.Value.ServiceType);
                if (serviceType == null)
                {
                    throw new Exception(String.Format("Cannot find Grain Service type {0} of Grain Service {1}", serviceConfig.Value.ServiceType, serviceConfig.Value.Name));
                }
                
                var grainServiceInterfaceType = serviceType.GetInterfaces().FirstOrDefault(x => x.GetInterfaces().Contains(typeof(IGrainService)));
                if (grainServiceInterfaceType == null)
                {
                    throw new Exception(String.Format("Cannot find an interface on {0} which implements IGrainService", serviceConfig.Value.ServiceType));
                }

                var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainServiceInterfaceType);
                var grainId = (IGrainIdentity)GrainId.GetGrainServiceGrainId(0, typeCode);
                var grainService = (GrainService)ActivatorUtilities.CreateInstance(this.Services, serviceType, grainId, serviceConfig.Value);
                RegisterSystemTarget(grainService);

                this.scheduler.QueueTask(() => grainService.Init(Services), grainService.SchedulingContext).WaitWithThrow(this.initTimeout);
                this.scheduler.QueueTask(grainService.Start, grainService.SchedulingContext).WaitWithThrow(this.initTimeout);
                if (this.logger.IsVerbose)
                {
                    this.logger.Verbose(String.Format("{0} Grain Service started successfully.", serviceConfig.Value.Name));
                }
            }
        }

        /// <summary>
        /// Load and initialize newly added stream providers. Remove providers that are not on the list that's being passed in.
        /// </summary>
        public async Task UpdateStreamProviders(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations)
        {
            IStreamProviderManagerAgent streamProviderUpdateAgent =
                runtimeClient.InternalGrainFactory.GetSystemTarget<IStreamProviderManagerAgent>(Constants.StreamProviderManagerAgentSystemTargetId, this.SiloAddress);

            await scheduler.QueueTask(() => streamProviderUpdateAgent.UpdateStreamProviders(streamProviderConfigurations), providerManagerSystemTarget.SchedulingContext)
                    .WithTimeout(initTimeout);
        }

        private void ConfigureThreadPoolAndServicePointSettings()
        {
#if !NETSTANDARD_TODO
            if (LocalConfig.MinDotNetThreadPoolSize > 0)
            {
                int workerThreads;
                int completionPortThreads;
                ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
                if (LocalConfig.MinDotNetThreadPoolSize > workerThreads ||
                    LocalConfig.MinDotNetThreadPoolSize > completionPortThreads)
                {
                    // if at least one of the new values is larger, set the new min values to be the larger of the prev. and new config value.
                    int newWorkerThreads = Math.Max(LocalConfig.MinDotNetThreadPoolSize, workerThreads);
                    int newCompletionPortThreads = Math.Max(LocalConfig.MinDotNetThreadPoolSize, completionPortThreads);
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
                LocalConfig.Expect100Continue, LocalConfig.DefaultConnectionLimit, LocalConfig.UseNagleAlgorithm);
            ServicePointManager.Expect100Continue = LocalConfig.Expect100Continue;
            ServicePointManager.DefaultConnectionLimit = LocalConfig.DefaultConnectionLimit;
            ServicePointManager.UseNagleAlgorithm = LocalConfig.UseNagleAlgorithm;
#endif
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
                        scheduler.QueueTask(this.membershipOracle.ShutDown, (this.membershipOracle as SystemTarget)?.SchedulingContext)
                            .WaitWithThrow(stopTimeout);
                    }
                    else
                    {
                        logger.Info(ErrorCode.SiloStopping, "Silo starting to Stop()");
                        // 1: Write "Stopping" state in the table + broadcast gossip msgs to re-read the table to everyone
                        scheduler.QueueTask(this.membershipOracle.Stop, (this.membershipOracle as SystemTarget)?.SchedulingContext)
                            .WaitWithThrow(stopTimeout);
                    }
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.SiloFailedToStopMembership, String.Format("Failed to {0} membership oracle. About to FastKill this silo.", operation), exc);
                    return; // will go to finally
                }

                if (reminderService != null)
                {
                    // 2: Stop reminder service
                    scheduler.QueueTask(reminderService.Stop, (reminderService as SystemTarget)?.SchedulingContext)
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
                logger.Info(ErrorCode.SiloStopped, "Silo is Stopped()");
                FastKill();                
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
                SafeExecute(() => scheduler.QueueTask( this.membershipOracle.KillMyself, (this.membershipOracle as SystemTarget)?.SchedulingContext)
                    .WaitWithThrow(stopTimeout));
            }

            // incoming messages
            SafeExecute(incomingSystemAgent.Stop);
            SafeExecute(incomingPingAgent.Stop);
            SafeExecute(incomingAgent.Stop);

            // timers
            SafeExecute(runtimeClient.Stop);
            if (platformWatchdog != null) 
                SafeExecute(platformWatchdog.Stop); // Silo may be dying before platformWatchdog was set up

            SafeExecute(scheduler.Stop);
            SafeExecute(scheduler.PrintStatistics);
            SafeExecute(activationDirectory.PrintActivationDirectory);
            SafeExecute(messageCenter.Stop);
            SafeExecute(siloStatistics.Stop);
            SafeExecute(GrainTypeManager.Stop);

            UnobservedExceptionsHandlerClass.ResetUnobservedExceptionHandler();

            SafeExecute(() => SystemStatus.Current = SystemStatus.Terminated);
            SafeExecute(LogManager.Close);
            SafeExecute(() => AppDomain.CurrentDomain.UnhandledException -= this.DomainUnobservedExceptionHandler);
            SafeExecute(() => this.assemblyProcessor?.Dispose());

            // Setting the event should be the last thing we do.
            // Do nothijng after that!
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
            LogManager.Flush();

            try
            {
                lock (lockable)
                {
                    if (!SystemStatus.Current.Equals(SystemStatus.Running)) return;
                    
                    SystemStatus.Current = SystemStatus.Stopping;
                }
                
                logger.Info(ErrorCode.SiloStopping, "Silo.HandleProcessExit() - starting to FastKill()");
                FastKill();
            }
            finally
            {
                LogManager.Close();
            }
        }

        private void UnobservedExceptionHandler(ISchedulingContext context, Exception exception)
        {
            var schedulingContext = context as SchedulingContext;
            if (schedulingContext == null)
            {
                if (context == null)
                    logger.Error(ErrorCode.Runtime_Error_100102, "Silo caught an UnobservedException with context==null.", exception);
                else
                    logger.Error(ErrorCode.Runtime_Error_100103, String.Format("Silo caught an UnobservedException with context of type different than OrleansContext. The type of the context is {0}. The context is {1}",
                        context.GetType(), context), exception);
            }
            else
            {
                logger.Error(ErrorCode.Runtime_Error_100104, String.Format("Silo caught an UnobservedException thrown by {0}.", schedulingContext.Activation), exception);
            }   
        }

        private void DomainUnobservedExceptionHandler(object context, UnhandledExceptionEventArgs args)
        {
            var exception = (Exception)args.ExceptionObject;
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
            foreach (var systemTarget in activationDirectory.AllSystemTargets())
                sb.AppendFormat("System target {0}:", ((ISystemTargetBase)systemTarget).GrainId.ToString()).AppendLine();               
            
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

