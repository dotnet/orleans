using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Hosting;
using Orleans.LogConsistency;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.LogConsistency;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Startup;
using Orleans.Runtime.Storage;
using Orleans.Services;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.Transactions;
using Orleans.Runtime.Versions;
using Orleans.Versions;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Serialization;

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
        private readonly ILogger logger;
        private TypeManager typeManager;

        private readonly TaskCompletionSource<int> siloTerminatedTask =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SiloStatisticsManager siloStatistics;
        private readonly InsideRuntimeClient runtimeClient;
        private StorageProviderManager storageProviderManager;
        private LogConsistencyProviderManager logConsistencyProviderManager;
        private StatisticsProviderManager statisticsProviderManager;
        private BootstrapProviderManager bootstrapProviderManager;
        private IReminderService reminderService;
        private ProviderManagerSystemTarget providerManagerSystemTarget;
        private readonly IMembershipOracle membershipOracle;
        private readonly IMultiClusterOracle multiClusterOracle;
        private readonly ExecutorService executorService;
        private Watchdog platformWatchdog;
        private readonly TimeSpan initTimeout;
        private readonly TimeSpan stopTimeout = TimeSpan.FromMinutes(1);
        private readonly Catalog catalog;
        private readonly List<IHealthCheckParticipant> healthCheckParticipants = new List<IHealthCheckParticipant>();
        private readonly object lockable = new object();
        private readonly GrainFactory grainFactory;
        private readonly IGrainRuntime grainRuntime;
        private readonly ILifecycleObserver siloLifecycle;

        private readonly ILoggerFactory loggerFactory;
        /// <summary>
        /// Gets the type of this 
        /// </summary>
        public SiloType Type => this.initializationParams.Type;
        internal string Name => this.initializationParams.Name;
        internal ClusterConfiguration OrleansConfig => this.initializationParams.ClusterConfig;
        internal GlobalConfiguration GlobalConfig => this.initializationParams.ClusterConfig.Globals;
        internal NodeConfiguration LocalConfig => this.initializationParams.NodeConfig;
        internal OrleansTaskScheduler LocalScheduler { get { return scheduler; } }
        internal ILocalGrainDirectory LocalGrainDirectory { get { return localGrainDirectory; } }
        internal IMultiClusterOracle LocalMultiClusterOracle { get { return multiClusterOracle; } }
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal ILogConsistencyProviderManager LogConsistencyProviderManager { get { return logConsistencyProviderManager; } }
        internal IStorageProviderManager StorageProviderManager { get { return storageProviderManager; } }
        internal IProviderManager StatisticsProviderManager { get { return statisticsProviderManager; } }
        internal IStreamProviderManager StreamProviderManager { get; private set; }
        internal IList<IBootstrapProvider> BootstrapProviders { get; private set; }
        internal ISiloPerformanceMetrics Metrics { get { return siloStatistics.MetricsTable; } }
        internal ICatalog Catalog => catalog;

        internal SystemStatus SystemStatus { get; set; }

        internal IServiceProvider Services { get; }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => this.initializationParams.SiloAddress;

        /// <summary>
        ///  Silo termination event used to signal shutdown of this silo.
        /// </summary>
        public WaitHandle SiloTerminatedEvent // one event for all types of termination (shutdown, stop and fast kill).
            => ((IAsyncResult)this.siloTerminatedTask.Task).AsyncWaitHandle;

        public Task SiloTerminated { get { return this.siloTerminatedTask.Task; } } // one event for all types of termination (shutdown, stop and fast kill).

        private SchedulingContext membershipOracleContext;
        private SchedulingContext multiClusterOracleContext;
        private SchedulingContext reminderServiceContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="Silo"/> class.
        /// </summary>
        /// <param name="name">Name of this silo.</param>
        /// <param name="siloType">Type of this silo.</param>
        /// <param name="config">Silo config data to be used for this silo.</param>
        public Silo(string name, SiloType siloType, ClusterConfiguration config)
            : this(new SiloInitializationParameters(name, siloType, config), null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Silo"/> class.
        /// </summary>
        /// <param name="initializationParams">The silo initialization parameters.</param>
        /// <param name="services">Dependency Injection container</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Should not Dispose of messageCenter in this method because it continues to run / exist after this point.")]
        internal Silo(SiloInitializationParameters initializationParams, IServiceProvider services)
        {
            string name = initializationParams.Name;
            ClusterConfiguration config = initializationParams.ClusterConfig;
            this.initializationParams = initializationParams;

            this.SystemStatus = SystemStatus.Creating;
            AsynchAgent.IsStarting = true;

            var startTime = DateTime.UtcNow;

            services?.GetService<TelemetryManager>()?.AddFromConfiguration(services, LocalConfig.TelemetryConfiguration);
            StatisticsCollector.Initialize(LocalConfig.StatisticsCollectionLevel);
            
            initTimeout = GlobalConfig.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), GlobalConfig.MaxJoinAttemptTime);
                stopTimeout = initTimeout;
            }

            var localEndpoint = this.initializationParams.SiloAddress.Endpoint;
            // Configure DI using Startup type
            if (services == null)
            {
                var serviceCollection = new ServiceCollection();
                serviceCollection.AddSingleton<Silo>(this);
                serviceCollection.AddSingleton(initializationParams);
                serviceCollection.AddLegacyClusterConfigurationSupport(config);
                serviceCollection.Configure<SiloIdentityOptions>(options => options.SiloName = name);
                var hostContext = new HostBuilderContext(new Dictionary<object, object>());
                DefaultSiloServices.AddDefaultServices(hostContext, serviceCollection);

                hostContext.GetApplicationPartManager()
                    .AddFromAppDomain()
                    .AddFromApplicationBaseDirectory();

                services = StartupBuilder.ConfigureStartup(this.LocalConfig.StartupTypeName, serviceCollection);
                services.GetService<TelemetryManager>()?.AddFromConfiguration(services, LocalConfig.TelemetryConfiguration);
            }

            services.GetService<SerializationManager>().RegisterSerializers(services.GetService<ApplicationPartManager>());

            this.Services = services;
            this.Services.InitializeSiloUnobservedExceptionsHandler();
            //set PropagateActivityId flag from node cofnig
            RequestContext.PropagateActivityId = this.initializationParams.NodeConfig.PropagateActivityId;
            this.loggerFactory = this.Services.GetRequiredService<ILoggerFactory>();
            logger = this.loggerFactory.CreateLogger<Silo>();

            logger.Info(ErrorCode.SiloGcSetting, "Silo starting with GC settings: ServerGC={0} GCLatencyMode={1}", GCSettings.IsServerGC, Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode));
            if (!GCSettings.IsServerGC)
            {
                logger.Warn(ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\">");
                logger.Warn(ErrorCode.SiloGcWarning, "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines).");
            }

            logger.Info(ErrorCode.SiloInitializing, "-------------- Initializing {0} silo on host {1} MachineName {2} at {3}, gen {4} --------------",
                this.initializationParams.Type, LocalConfig.DNSHostName, Environment.MachineName, localEndpoint, this.initializationParams.SiloAddress.Generation);
            logger.Info(ErrorCode.SiloInitConfig, "Starting silo {0} with the following configuration= " + Environment.NewLine + "{1}",
                name, config.ToString(name));

            var siloMessagingOptions = this.Services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            BufferPool.InitGlobalBufferPool(siloMessagingOptions);

            try
            {
                grainFactory = Services.GetRequiredService<GrainFactory>();
            }
            catch (InvalidOperationException exc)
            {
                logger.Error(ErrorCode.SiloStartError, "Exception during Silo.Start, GrainFactory was not registered in Dependency Injection container", exc);
                throw;
            }

            // Performance metrics
            siloStatistics = Services.GetRequiredService<SiloStatisticsManager>();

            // The scheduler
            scheduler = Services.GetRequiredService<OrleansTaskScheduler>();
            healthCheckParticipants.Add(scheduler);

            runtimeClient = Services.GetRequiredService<InsideRuntimeClient>();

            // Initialize the message center
            messageCenter = Services.GetRequiredService<MessageCenter>();
            var dispatcher = this.Services.GetRequiredService<Dispatcher>();
            messageCenter.RerouteHandler = dispatcher.RerouteMessage;
            messageCenter.SniffIncomingMessage = runtimeClient.SniffIncomingMessage;

            // GrainRuntime can be created only here, after messageCenter was created.
            grainRuntime = Services.GetRequiredService<IGrainRuntime>();
            StreamProviderManager = Services.GetRequiredService<IStreamProviderManager>();

            // Now the router/directory service
            // This has to come after the message center //; note that it then gets injected back into the message center.;
            localGrainDirectory = Services.GetRequiredService<LocalGrainDirectory>();

            // Now the activation directory.
            activationDirectory = Services.GetRequiredService<ActivationDirectory>();

            // Now the consistent ring provider
            RingProvider = Services.GetRequiredService<IConsistentRingProvider>();

            catalog = Services.GetRequiredService<Catalog>();
            siloStatistics.MetricsTable.Scheduler = scheduler;
            siloStatistics.MetricsTable.ActivationDirectory = activationDirectory;
            siloStatistics.MetricsTable.ActivationCollector = catalog.ActivationCollector;
            siloStatistics.MetricsTable.MessageCenter = messageCenter;

            executorService = Services.GetRequiredService<ExecutorService>();

            // Now the incoming message agents
            var messageFactory = this.Services.GetRequiredService<MessageFactory>();
            incomingSystemAgent = new IncomingMessageAgent(Message.Categories.System, messageCenter, activationDirectory, scheduler, catalog.Dispatcher, messageFactory, executorService, this.loggerFactory);
            incomingPingAgent = new IncomingMessageAgent(Message.Categories.Ping, messageCenter, activationDirectory, scheduler, catalog.Dispatcher, messageFactory, executorService, this.loggerFactory);
            incomingAgent = new IncomingMessageAgent(Message.Categories.Application, messageCenter, activationDirectory, scheduler, catalog.Dispatcher, messageFactory, executorService, this.loggerFactory);

            membershipOracle = Services.GetRequiredService<IMembershipOracle>();

            if (!this.GlobalConfig.HasMultiClusterNetwork)
            {
                logger.Info("Skip multicluster oracle creation (no multicluster network configured)");
            }
            else
            {
                multiClusterOracle = Services.GetRequiredService<IMultiClusterOracle>();
            }

            this.SystemStatus = SystemStatus.Created;
            AsynchAgent.IsStarting = false;

            StringValueStatistic.FindOrCreate(StatisticNames.SILO_START_TIME,
                () => LogFormatter.PrintDate(startTime)); // this will help troubleshoot production deployment when looking at MDS logs.

            var fullSiloLifecycle = this.Services.GetRequiredService<SiloLifecycle>();
            this.siloLifecycle = fullSiloLifecycle;
            IEnumerable<ILifecycleParticipant<ISiloLifecycle>> lifecyleParticipants = this.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach(ILifecycleParticipant<ISiloLifecycle> participant in lifecyleParticipants)
            {
                participant.Participate(fullSiloLifecycle);
            }
            this.Participate(fullSiloLifecycle);

            logger.Info(ErrorCode.SiloInitializingFinished, "-------------- Started silo {0}, ConsistentHashCode {1:X} --------------", SiloAddress.ToLongString(), SiloAddress.GetConsistentHashCode());
        }

        public void Start()
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await this.siloLifecycle.OnStart(cancellationToken);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.SiloStartError, "Exception during Silo.Start", exc);
                throw;
            }
        }

        private void CreateSystemTargets()
        {
            logger.Debug("Creating System Targets for this silo.");

            logger.Debug("Creating {0} System Target", "SiloControl");
            var siloControl = ActivatorUtilities.CreateInstance<SiloControl>(Services);
            RegisterSystemTarget(siloControl);

            logger.Debug("Creating {0} System Target", "StreamProviderUpdateAgent");
            RegisterSystemTarget(new StreamProviderManagerAgent(this, Services.GetRequiredService<IStreamProviderRuntime>(), this.loggerFactory));

            logger.Debug("Creating {0} System Target", "ProtocolGateway");
            RegisterSystemTarget(new ProtocolGateway(this.SiloAddress, this.loggerFactory));

            logger.Debug("Creating {0} System Target", "DeploymentLoadPublisher");
            RegisterSystemTarget(Services.GetRequiredService<DeploymentLoadPublisher>());
            
            logger.Debug("Creating {0} System Target", "RemoteGrainDirectory + CacheValidator");
            RegisterSystemTarget(LocalGrainDirectory.RemoteGrainDirectory);
            RegisterSystemTarget(LocalGrainDirectory.CacheValidator);

            logger.Debug("Creating {0} System Target", "RemoteClusterGrainDirectory");
            RegisterSystemTarget(LocalGrainDirectory.RemoteClusterGrainDirectory);

            logger.Debug("Creating {0} System Target", "ClientObserverRegistrar + TypeManager");

            this.RegisterSystemTarget(this.Services.GetRequiredService<ClientObserverRegistrar>());
            var implicitStreamSubscriberTable = Services.GetRequiredService<ImplicitStreamSubscriberTable>();
            var versionDirectorManager = this.Services.GetRequiredService<CachedVersionSelectorManager>();
            var grainTypeManager = this.Services.GetRequiredService<GrainTypeManager>();
            typeManager = new TypeManager(SiloAddress, grainTypeManager, membershipOracle, LocalScheduler, GlobalConfig.TypeMapRefreshInterval, implicitStreamSubscriberTable, this.grainFactory, versionDirectorManager,
                this.loggerFactory);
            this.RegisterSystemTarget(typeManager);

            logger.Debug("Creating {0} System Target", "MembershipOracle");
            if (this.membershipOracle is SystemTarget)
            {
                RegisterSystemTarget((SystemTarget)membershipOracle);
            }

            if (multiClusterOracle != null && multiClusterOracle is SystemTarget)
            {
                logger.Debug("Creating {0} System Target", "MultiClusterOracle");
                RegisterSystemTarget((SystemTarget)multiClusterOracle);
            }

            var transactionAgent = this.Services.GetRequiredService<ITransactionAgent>() as SystemTarget;
            if (transactionAgent != null)
            {
                logger.Debug("Creating {0} System Target", "TransactionAgent");
                RegisterSystemTarget(transactionAgent);
            }

            logger.Debug("Finished creating System Targets for this silo.");
        }

        private async Task InjectDependencies()
        {
            healthCheckParticipants.Add(membershipOracle);

            catalog.SiloStatusOracle = this.membershipOracle;
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
                                          .CreateReminderService(this, initTimeout, this.runtimeClient);
                var reminderServiceSystemTarget = this.reminderService as SystemTarget;
                if (reminderServiceSystemTarget != null) RegisterSystemTarget(reminderServiceSystemTarget);
            }

            RegisterSystemTarget(catalog);
            await scheduler.QueueAction(catalog.Start, catalog.SchedulingContext)
                .WithTimeout(initTimeout);

            // SystemTarget for provider init calls
            providerManagerSystemTarget = Services.GetRequiredService<ProviderManagerSystemTarget>();

            RegisterSystemTarget(providerManagerSystemTarget);
        }

        private Task OnRuntimeInitializeStart(CancellationToken ct)
        {
            lock (lockable)
            {
                if (!this.SystemStatus.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(String.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", this.SystemStatus));

                this.SystemStatus = SystemStatus.Starting;
            }

            logger.Info(ErrorCode.SiloStarting, "Silo Start()");

            // Hook up to receive notification of process exit / Ctrl-C events
            AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
            if (GlobalConfig.FastKillOnCancelKeyPress)
                Console.CancelKeyPress += HandleProcessExit;

            //TODO: setup thead pool directly to lifecycle
            ConfigureThreadPoolAndServicePointSettings();

            return Task.CompletedTask;
        }

        private async Task OnRuntimeServicesStart(CancellationToken ct)
        {
            //TODO: Setup all (or as many as possible) of the class started in this call to work directly with lifecyce

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
            var grainTypeManager = Services.GetRequiredService<GrainTypeManager>();
            implicitStreamSubscriberTable.InitImplicitStreamSubscribers(grainTypeManager.GrainClassTypeData.Select(t => t.Value.Type).ToArray());

            var siloProviderRuntime = Services.GetRequiredService<SiloProviderRuntime>();
            runtimeClient.CurrentStreamProviderRuntime = siloProviderRuntime;
            statisticsProviderManager = this.Services.GetRequiredService<StatisticsProviderManager>();
            string statsProviderName =  await statisticsProviderManager.LoadProvider(GlobalConfig.ProviderConfigurations)
                .WithTimeout(initTimeout);
            if (statsProviderName != null)
                LocalConfig.StatisticsProviderName = statsProviderName;

            // can call SetSiloMetricsTableDataManager only after MessageCenter is created (dependency on this.SiloAddress).
            await siloStatistics.SetSiloStatsTableDataManager(this, LocalConfig).WithTimeout(initTimeout);
            await siloStatistics.SetSiloMetricsTableDataManager(this, LocalConfig).WithTimeout(initTimeout);


            // This has to follow the above steps that start the runtime components
            CreateSystemTargets();

            await InjectDependencies();

            // Validate the configuration.
            GlobalConfig.Application.ValidateConfiguration(logger);

            // Initialize storage providers once we have a basic silo runtime environment operating
            storageProviderManager = this.Services.GetRequiredService<StorageProviderManager>();
            await scheduler.QueueTask(
                () => storageProviderManager.LoadStorageProviders(GlobalConfig.ProviderConfigurations),
                providerManagerSystemTarget.SchedulingContext)
                    .WithTimeout(initTimeout);

            ITransactionAgent transactionAgent = this.Services.GetRequiredService<ITransactionAgent>();
            ISchedulingContext transactionAgentContext = (transactionAgent as SystemTarget)?.SchedulingContext;
            await scheduler.QueueTask(transactionAgent.Start, transactionAgentContext)
                        .WithTimeout(initTimeout);

            var versionStore = Services.GetService<IVersionStore>() as GrainVersionStore;
            versionStore?.SetStorageManager(storageProviderManager);
            logger.Debug("Storage provider manager created successfully."); 

            // Initialize log consistency providers once we have a basic silo runtime environment operating
            logConsistencyProviderManager = this.Services.GetRequiredService<LogConsistencyProviderManager>();
            await scheduler.QueueTask(
                () => logConsistencyProviderManager.LoadLogConsistencyProviders(GlobalConfig.ProviderConfigurations),
                providerManagerSystemTarget.SchedulingContext)
                    .WithTimeout(initTimeout);
            logger.Debug("Log consistency provider manager created successfully."); 

            // Load and init stream providers before silo becomes active
            var siloStreamProviderManager = (StreamProviderManager) this.Services.GetRequiredService<IStreamProviderManager>();
            await scheduler.QueueTask(
                () => siloStreamProviderManager.LoadStreamProviders(GlobalConfig.ProviderConfigurations, siloProviderRuntime),
                    providerManagerSystemTarget.SchedulingContext)
                        .WithTimeout(initTimeout);
            runtimeClient.CurrentStreamProviderManager = siloStreamProviderManager;
            logger.Debug("Stream provider manager created successfully."); 

            // Load and init grain services before silo becomes active.
            await CreateGrainServices(GlobalConfig.GrainServiceConfigurations);

            this.membershipOracleContext = (this.membershipOracle as SystemTarget)?.SchedulingContext ??
                                       this.providerManagerSystemTarget.SchedulingContext;
            await scheduler.QueueTask(() => this.membershipOracle.Start(), this.membershipOracleContext)
                .WithTimeout(initTimeout);
            logger.Debug("Local silo status oracle created successfully."); 
            await scheduler.QueueTask(this.membershipOracle.BecomeActive, this.membershipOracleContext)
                .WithTimeout(initTimeout);
            logger.Debug("Local silo status oracle became active successfully."); 
            await scheduler.QueueTask(() => this.typeManager.Initialize(versionStore), this.typeManager.SchedulingContext)
                .WithTimeout(this.initTimeout);

            //if running in multi cluster scenario, start the MultiClusterNetwork Oracle
            if (GlobalConfig.HasMultiClusterNetwork) 
            {
                logger.Info("Starting multicluster oracle with my ServiceId={0} and ClusterId={1}.",
                    GlobalConfig.ServiceId, GlobalConfig.ClusterId);

                this.multiClusterOracleContext = (multiClusterOracle as SystemTarget)?.SchedulingContext ??
                                                          this.providerManagerSystemTarget.SchedulingContext;
                await scheduler.QueueTask(() => multiClusterOracle.Start(), multiClusterOracleContext)
                                    .WithTimeout(initTimeout);
                logger.Debug("multicluster oracle created successfully."); 
            }

            try
            {
                this.siloStatistics.Start(this.LocalConfig);
                logger.Debug("Silo statistics manager started successfully."); 

                // Finally, initialize the deployment load collector, for grains with load-based placement
                var deploymentLoadPublisher = Services.GetRequiredService<DeploymentLoadPublisher>();
                await this.scheduler.QueueTask(deploymentLoadPublisher.Start, deploymentLoadPublisher.SchedulingContext)
                    .WithTimeout(this.initTimeout);
                logger.Debug("Silo deployment load publisher started successfully."); 

                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                this.platformWatchdog = new Watchdog(this.LocalConfig.StatisticsLogWriteInterval, this.healthCheckParticipants, this.executorService, this.loggerFactory);
                this.platformWatchdog.Start();
                if (this.logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Silo platform watchdog started successfully."); }

                if (this.reminderService != null)
                {
                    // so, we have the view of the membership in the consistentRingProvider. We can start the reminder service
                    this.reminderServiceContext = (this.reminderService as SystemTarget)?.SchedulingContext ??
                                                           this.providerManagerSystemTarget.SchedulingContext;
                    await this.scheduler.QueueTask(this.reminderService.Start, this.reminderServiceContext)
                        .WithTimeout(this.initTimeout);
                    this.logger.Debug("Reminder service started successfully.");
                }

                this.bootstrapProviderManager = this.Services.GetRequiredService<BootstrapProviderManager>();
                await this.scheduler.QueueTask(
                    () => this.bootstrapProviderManager.LoadAppBootstrapProviders(siloProviderRuntime, this.GlobalConfig.ProviderConfigurations),
                    this.providerManagerSystemTarget.SchedulingContext)
                        .WithTimeout(this.initTimeout);
                this.BootstrapProviders = this.bootstrapProviderManager.GetProviders(); // Data hook for testing & diagnotics

                logger.Debug("App bootstrap calls done successfully."); 

                // Start stream providers after silo is active (so the pulling agents don't start sending messages before silo is active).
                // also after bootstrap provider started so bootstrap provider can initialize everything stream before events from this silo arrive.
                await this.scheduler.QueueTask(siloStreamProviderManager.StartStreamProviders, this.providerManagerSystemTarget.SchedulingContext)
                    .WithTimeout(this.initTimeout);
                logger.Debug("Stream providers started successfully."); 

                // Now that we're active, we can start the gateway
                var mc = this.messageCenter as MessageCenter;
                mc?.StartGateway(this.Services.GetRequiredService<ClientObserverRegistrar>());
               logger.Debug("Message gateway service started successfully."); 

                this.SystemStatus = SystemStatus.Running;
            }
            catch (Exception exc)
            {
                this.SafeExecute(() => this.logger.Error(ErrorCode.Runtime_Error_100330, String.Format("Error starting silo {0}. Going to FastKill().", this.SiloAddress), exc));
                throw;
            }
            if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Silo.Start complete: System status = {0}", this.SystemStatus); }
        }

        private async Task CreateGrainServices(GrainServiceConfigurations grainServiceConfigurations)
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

                await this.scheduler.QueueTask(() => grainService.Init(Services), grainService.SchedulingContext).WithTimeout(this.initTimeout);
                await this.scheduler.QueueTask(grainService.Start, grainService.SchedulingContext).WithTimeout(this.initTimeout);
                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Debug(String.Format("{0} Grain Service started successfully.", serviceConfig.Value.Name));
                }
            }
        }

        private void ConfigureThreadPoolAndServicePointSettings()
        {
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
        }

        /// <summary>
        /// Gracefully stop the run time system only, but not the application. 
        /// Applications requests would be abruptly terminated, while the internal system state gracefully stopped and saved as much as possible.
        /// Grains are not deactivated.
        /// </summary>
        public void Stop()
        {
            var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            StopAsync(cancellationSource.Token).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gracefully stop the run time system and the application. 
        /// All grains will be properly deactivated.
        /// All in-flight applications requests would be awaited and finished gracefully.
        /// </summary>
        public void Shutdown()
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gracefully stop the run time system only, but not the application. 
        /// Applications requests would be abruptly terminated, while the internal system state gracefully stopped and saved as much as possible.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            bool gracefully = !cancellationToken.IsCancellationRequested;
            string operation = gracefully ? "Shutdown()" : "Stop()";
            bool stopAlreadyInProgress = false;
            lock (lockable)
            {
                if (this.SystemStatus.Equals(SystemStatus.Stopping) ||
                    this.SystemStatus.Equals(SystemStatus.ShuttingDown) ||
                    this.SystemStatus.Equals(SystemStatus.Terminated))
                {
                    stopAlreadyInProgress = true;
                    // Drop through to wait below
                }
                else if (!this.SystemStatus.Equals(SystemStatus.Running))
                {
                    throw new InvalidOperationException(String.Format("Calling Silo.{0} on a silo which is not in the Running state. This silo is in the {1} state.", operation, this.SystemStatus));
                }
                else
                {
                    if (gracefully)
                        this.SystemStatus = SystemStatus.ShuttingDown;
                    else
                        this.SystemStatus = SystemStatus.Stopping;
                }
            }

            if (stopAlreadyInProgress)
            {
                logger.Info(ErrorCode.SiloStopInProgress, "Silo termination is in progress - Will wait for it to finish");
                var pause = TimeSpan.FromSeconds(1);
                while (!this.SystemStatus.Equals(SystemStatus.Terminated))
                {
                    logger.Info(ErrorCode.WaitingForSiloStop, "Waiting {0} for termination to complete", pause);
                    Thread.Sleep(pause);
                }
                return this.siloTerminatedTask.Task;
            }
            return this.siloLifecycle.OnStop(cancellationToken);
        }

        private async Task OnRuntimeServicesStop(CancellationToken cancellationToken)
        {
            bool gracefully = !cancellationToken.IsCancellationRequested;
            string operation = gracefully ? "Shutdown()" : "Stop()";
            try
            {
                if (gracefully)
                {
                    logger.Info(ErrorCode.SiloShuttingDown, "Silo starting to Shutdown()");
                    // 1: Write "ShutDown" state in the table + broadcast gossip msgs to re-read the table to everyone
                    await scheduler.QueueTask(this.membershipOracle.ShutDown, this.membershipOracleContext)
                        .WithTimeout(stopTimeout);
                }
                else
                {
                    logger.Info(ErrorCode.SiloStopping, "Silo starting to Stop()");
                    // 1: Write "Stopping" state in the table + broadcast gossip msgs to re-read the table to everyone
                    await scheduler.QueueTask(this.membershipOracle.Stop, this.membershipOracleContext)
                        .WithTimeout(stopTimeout);
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
                await scheduler.QueueTask(reminderService.Stop, this.reminderServiceContext)
                    .WithTimeout(stopTimeout);
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
                var siloStreamProviderManager = (StreamProviderManager) StreamProviderManager;
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

        private Task OnRuntimeInitializeStop(CancellationToken ct)
        {
            // 10, 11, 12: Write Dead in the table, Drain scheduler, Stop msg center, ...
            logger.Info(ErrorCode.SiloStopped, "Silo is Stopped()");

            SafeExecute(() => scheduler.QueueTask( this.membershipOracle.KillMyself, this.membershipOracleContext)
                .WaitWithThrow(stopTimeout));

            // incoming messages
            SafeExecute(incomingSystemAgent.Stop);
            SafeExecute(incomingPingAgent.Stop);
            SafeExecute(incomingAgent.Stop);

            // timers
            if (platformWatchdog != null) 
                SafeExecute(platformWatchdog.Stop); // Silo may be dying before platformWatchdog was set up

            SafeExecute(scheduler.Stop);
            SafeExecute(scheduler.PrintStatistics);
            SafeExecute(activationDirectory.PrintActivationDirectory);
            SafeExecute(messageCenter.Stop);
            SafeExecute(siloStatistics.Stop);

            SafeExecute(() => this.SystemStatus = SystemStatus.Terminated);
            SafeExecute(() => (this.Services as IDisposable)?.Dispose());

            // Setting the event should be the last thing we do.
            // Do nothing after that!
            this.siloTerminatedTask.SetResult(0);
            return Task.CompletedTask;
        }

        private void SafeExecute(Action action)
        {
            Utils.SafeExecute(action, logger, "Silo.Stop");
        }

        private void HandleProcessExit(object sender, EventArgs e)
        {
            // NOTE: We need to minimize the amount of processing occurring on this code path -- we only have under approx 2-3 seconds before process exit will occur
            logger.Warn(ErrorCode.Runtime_Error_100220, "Process is exiting");
            
            lock (lockable)
            {
                if (!this.SystemStatus.Equals(SystemStatus.Running)) return;
                    
                this.SystemStatus = SystemStatus.Stopping;
            }
                
            logger.Info(ErrorCode.SiloStopping, "Silo.HandleProcessExit() - starting to FastKill()");
            Stop();
        }

        internal void RegisterSystemTarget(SystemTarget target)
        {
            var providerRuntime = this.Services.GetRequiredService<SiloProviderRuntime>();
            providerRuntime.RegisterSystemTarget(target);
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
                    var workItemGroup = scheduler.GetWorkItemGroup(activationData.SchedulingContext);
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

        private void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(SiloLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);
            lifecycle.Subscribe(SiloLifecycleStage.RuntimeServices, OnRuntimeServicesStart, OnRuntimeServicesStop);
        }
    }

    // A dummy system target to use for scheduling context for provider Init calls, to allow them to make grain calls
    internal class ProviderManagerSystemTarget : SystemTarget
    {
        public ProviderManagerSystemTarget(ILocalSiloDetails localSiloDetails, ILoggerFactory loggerFactory)
            : base(Constants.ProviderManagerSystemTargetId, localSiloDetails.SiloAddress, loggerFactory)
        {
        }
    }
}

