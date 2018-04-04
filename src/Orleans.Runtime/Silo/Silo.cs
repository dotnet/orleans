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
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.LogConsistency;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.Providers;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Scheduler;
using Orleans.Services;
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

        private readonly ILocalSiloDetails siloDetails;
        private readonly ClusterOptions clusterOptions;
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
        private IReminderService reminderService;
        private SystemTarget fallbackScheduler;
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
        private readonly ISiloLifecycleSubject siloLifecycle;
        private List<GrainService> grainServices = new List<GrainService>();

        private readonly ILoggerFactory loggerFactory;
        /// <summary>
        /// Gets the type of this 
        /// </summary>
        internal string Name => this.siloDetails.Name;
        internal OrleansTaskScheduler LocalScheduler { get { return scheduler; } }
        internal ILocalGrainDirectory LocalGrainDirectory { get { return localGrainDirectory; } }
        internal IMultiClusterOracle LocalMultiClusterOracle { get { return multiClusterOracle; } }
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal ICatalog Catalog => catalog;

        internal SystemStatus SystemStatus { get; set; }

        internal IServiceProvider Services { get; }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => this.siloDetails.SiloAddress;

        /// <summary>
        ///  Silo termination event used to signal shutdown of this silo.
        /// </summary>
        public WaitHandle SiloTerminatedEvent // one event for all types of termination (shutdown, stop and fast kill).
            => ((IAsyncResult)this.siloTerminatedTask.Task).AsyncWaitHandle;

        public Task SiloTerminated { get { return this.siloTerminatedTask.Task; } } // one event for all types of termination (shutdown, stop and fast kill).

        private SchedulingContext membershipOracleContext;
        private SchedulingContext multiClusterOracleContext;
        private SchedulingContext reminderServiceContext;
        private LifecycleSchedulingSystemTarget lifecycleSchedulingSystemTarget;

        /// <summary>
        /// Initializes a new instance of the <see cref="Silo"/> class.
        /// </summary>
        /// <param name="siloDetails">The silo initialization parameters</param>
        /// <param name="services">Dependency Injection container</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Should not Dispose of messageCenter in this method because it continues to run / exist after this point.")]
        public Silo(ILocalSiloDetails siloDetails, IServiceProvider services)
        {
            string name = siloDetails.Name;
            // Temporarily still require this. Hopefuly gone when 2.0 is released.
            this.siloDetails = siloDetails;
            this.SystemStatus = SystemStatus.Creating;
            AsynchAgent.IsStarting = true; // todo. use ISiloLifecycle instead?

            var startTime = DateTime.UtcNow;

            IOptions<SiloStatisticsOptions> statisticsOptions = services.GetRequiredService<IOptions<SiloStatisticsOptions>>();
            StatisticsCollector.Initialize(statisticsOptions.Value.CollectionLevel);

            IOptions<ClusterMembershipOptions> clusterMembershipOptions = services.GetRequiredService<IOptions<ClusterMembershipOptions>>();
            initTimeout = clusterMembershipOptions.Value.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), clusterMembershipOptions.Value.MaxJoinAttemptTime);
                stopTimeout = initTimeout;
            }

            var localEndpoint = this.siloDetails.SiloAddress.Endpoint;

            services.GetService<SerializationManager>().RegisterSerializers(services.GetService<IApplicationPartManager>());

            this.Services = services;
            this.Services.InitializeSiloUnobservedExceptionsHandler();
            //set PropagateActivityId flag from node config
            IOptions<SiloMessagingOptions> messagingOptions = services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            RequestContext.PropagateActivityId = messagingOptions.Value.PropagateActivityId;
            this.loggerFactory = this.Services.GetRequiredService<ILoggerFactory>();
            logger = this.loggerFactory.CreateLogger<Silo>();

            logger.Info(ErrorCode.SiloGcSetting, "Silo starting with GC settings: ServerGC={0} GCLatencyMode={1}", GCSettings.IsServerGC, Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode));
            if (!GCSettings.IsServerGC)
            {
                logger.Warn(ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\">");
                logger.Warn(ErrorCode.SiloGcWarning, "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines).");
            }

            logger.Info(ErrorCode.SiloInitializing, "-------------- Initializing silo on host {0} MachineName {1} at {2}, gen {3} --------------",
                this.siloDetails.DnsHostName, Environment.MachineName, localEndpoint, this.siloDetails.SiloAddress.Generation);
            logger.Info(ErrorCode.SiloInitConfig, "Starting silo {0}", name);

            var siloMessagingOptions = this.Services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            BufferPool.InitGlobalBufferPool(siloMessagingOptions.Value);

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

            // Now the router/directory service
            // This has to come after the message center //; note that it then gets injected back into the message center.;
            localGrainDirectory = Services.GetRequiredService<LocalGrainDirectory>();

            // Now the activation directory.
            activationDirectory = Services.GetRequiredService<ActivationDirectory>();

            // Now the consistent ring provider
            RingProvider = Services.GetRequiredService<IConsistentRingProvider>();

            catalog = Services.GetRequiredService<Catalog>();

            executorService = Services.GetRequiredService<ExecutorService>();

            // Now the incoming message agents
            var messageFactory = this.Services.GetRequiredService<MessageFactory>();
            incomingSystemAgent = new IncomingMessageAgent(Message.Categories.System, messageCenter, activationDirectory, scheduler, catalog.Dispatcher, messageFactory, executorService, this.loggerFactory);
            incomingPingAgent = new IncomingMessageAgent(Message.Categories.Ping, messageCenter, activationDirectory, scheduler, catalog.Dispatcher, messageFactory, executorService, this.loggerFactory);
            incomingAgent = new IncomingMessageAgent(Message.Categories.Application, messageCenter, activationDirectory, scheduler, catalog.Dispatcher, messageFactory, executorService, this.loggerFactory);

            membershipOracle = Services.GetRequiredService<IMembershipOracle>();
            this.clusterOptions = Services.GetRequiredService<IOptions<ClusterOptions>>().Value;
            var multiClusterOptions = Services.GetRequiredService<IOptions<MultiClusterOptions>>().Value;

            if (!multiClusterOptions.HasMultiClusterNetwork)
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

            this.siloLifecycle = this.Services.GetRequiredService<ISiloLifecycleSubject>();
            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<ISiloLifecycle>> lifecycleParticipants = this.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach(ILifecycleParticipant<ISiloLifecycle> participant in lifecycleParticipants)
            {
                participant?.Participate(this.siloLifecycle);
            }
            // register all named lifecycle participants
            IKeyedServiceCollection<string, ILifecycleParticipant<ISiloLifecycle>> namedLifecycleParticipantCollection = this.Services.GetService<IKeyedServiceCollection<string,ILifecycleParticipant<ISiloLifecycle>>>();
            foreach (ILifecycleParticipant<ISiloLifecycle> participant in namedLifecycleParticipantCollection
                ?.GetServices(this.Services)
                ?.Select(s => s.GetService(this.Services)))
            {
                participant?.Participate(this.siloLifecycle);
            }

            // add self to lifecycle
            this.Participate(this.siloLifecycle);

            logger.Info(ErrorCode.SiloInitializingFinished, "-------------- Started silo {0}, ConsistentHashCode {1:X} --------------", SiloAddress.ToLongString(), SiloAddress.GetConsistentHashCode());
        }

        public void Start()
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartTaskWithPerfAnalysis("Start Scheduler", scheduler.Start, new Stopwatch());

            // SystemTarget for provider init calls
            this.lifecycleSchedulingSystemTarget = Services.GetRequiredService<LifecycleSchedulingSystemTarget>();
            this.fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(lifecycleSchedulingSystemTarget);

            try
            {
                await this.scheduler.QueueTask(() => this.siloLifecycle.OnStart(cancellationToken), this.lifecycleSchedulingSystemTarget.SchedulingContext);
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
            IOptions<TypeManagementOptions> typeManagementOptions = this.Services.GetRequiredService<IOptions<TypeManagementOptions>>();
            typeManager = new TypeManager(SiloAddress, grainTypeManager, membershipOracle, LocalScheduler, typeManagementOptions.Value.TypeMapRefreshInterval, implicitStreamSubscriberTable, this.grainFactory, versionDirectorManager,
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

            var reminderTable = Services.GetService<IReminderTable>();
            if (reminderTable != null)
            {
                logger.Info($"Creating reminder grain service for type={reminderTable.GetType()}");
                
                // Start the reminder service system target
                reminderService = new LocalReminderService(this, reminderTable, this.initTimeout, this.loggerFactory); ;
                RegisterSystemTarget((SystemTarget)reminderService);
            }

            RegisterSystemTarget(catalog);
            await scheduler.QueueAction(catalog.Start, catalog.SchedulingContext)
                .WithTimeout(initTimeout, $"Starting Catalog failed due to timeout {initTimeout}");

            // SystemTarget for provider init calls
            this.fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(fallbackScheduler);
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

            var processExitHandlingOptions = this.Services.GetService<IOptions<ProcessExitHandlingOptions>>().Value;
            if(processExitHandlingOptions.FastKillOnProcessExit)
                AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;

            //TODO: setup thead pool directly to lifecycle
            StartTaskWithPerfAnalysis("ConfigureThreadPoolAndServicePointSettings",
                this.ConfigureThreadPoolAndServicePointSettings, Stopwatch.StartNew());
            return Task.CompletedTask;
        }

        private void StartTaskWithPerfAnalysis(string taskName, Action task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            task.Invoke();
            stopWatch.Stop();
            this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"{taskName} took {stopWatch.ElapsedMilliseconds} Milliseconds to finish");
        }

        private async Task StartAsyncTaskWithPerfAnalysis(string taskName, Func<Task> task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            await task.Invoke();
            stopWatch.Stop();
            this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"{taskName} took {stopWatch.ElapsedMilliseconds} Milliseconds to finish");
        }

        private async Task OnRuntimeServicesStart(CancellationToken ct)
        {
            //TODO: Setup all (or as many as possible) of the class started in this call to work directly with lifecyce
            var stopWatch = Stopwatch.StartNew();
            // The order of these 4 is pretty much arbitrary.
            StartTaskWithPerfAnalysis("Start Message center",messageCenter.Start,stopWatch);
            StartTaskWithPerfAnalysis("Start Incoming message agents", IncomingMessageAgentsStart, stopWatch);
            void IncomingMessageAgentsStart()
            {
                incomingPingAgent.Start();
                incomingSystemAgent.Start();
                incomingAgent.Start();
            } 

            StartTaskWithPerfAnalysis("Start local grain directory", LocalGrainDirectory.Start,stopWatch);

            // Set up an execution context for this thread so that the target creation steps can use asynch values.
            RuntimeContext.InitializeMainThread();

            StartTaskWithPerfAnalysis("Init implicit stream subscribe table", InitImplicitStreamSubscribeTable, stopWatch);
            void InitImplicitStreamSubscribeTable()
            {             
                // Initialize the implicit stream subscribers table.
                var implicitStreamSubscriberTable = Services.GetRequiredService<ImplicitStreamSubscriberTable>();
                var grainTypeManager = Services.GetRequiredService<GrainTypeManager>();
                implicitStreamSubscriberTable.InitImplicitStreamSubscribers(grainTypeManager.GrainClassTypeData.Select(t => t.Value.Type).ToArray());
            }

            this.runtimeClient.CurrentStreamProviderRuntime = this.Services.GetRequiredService<SiloProviderRuntime>();
            
            // This has to follow the above steps that start the runtime components
            await StartAsyncTaskWithPerfAnalysis("Create system targets and inject dependencies", () =>
            {
                CreateSystemTargets();
                return InjectDependencies();
            }, stopWatch);

            // Validate the configuration.
            // TODO - refactor validation - jbragg
            //GlobalConfig.Application.ValidateConfiguration(logger);
        }

        private async Task OnRuntimeGrainServicesStart(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            await StartAsyncTaskWithPerfAnalysis("Init transaction agent", InitTransactionAgent, stopWatch);
            async Task InitTransactionAgent()
            {
                ITransactionAgent transactionAgent = this.Services.GetRequiredService<ITransactionAgent>();
                ISchedulingContext transactionAgentContext = (transactionAgent as SystemTarget)?.SchedulingContext;
                await scheduler.QueueTask(transactionAgent.Start, transactionAgentContext)
                    .WithTimeout(initTimeout, $"Starting TransactionAgent failed due to timeout {initTimeout}");
            }

            // Load and init grain services before silo becomes active.
            await StartAsyncTaskWithPerfAnalysis("Init grain services",
                () => CreateGrainServices(), stopWatch);

            this.membershipOracleContext = (this.membershipOracle as SystemTarget)?.SchedulingContext ??
                                       this.fallbackScheduler.SchedulingContext;

            await StartAsyncTaskWithPerfAnalysis("Starting local silo status oracle", StartMembershipOracle, stopWatch);

            async Task StartMembershipOracle()
            {
                await scheduler.QueueTask(() => this.membershipOracle.Start(), this.membershipOracleContext)
                    .WithTimeout(initTimeout, $"Starting MembershipOracle failed due to timeout {initTimeout}");
                logger.Debug("Local silo status oracle created successfully.");
            }

            var versionStore = Services.GetService<IVersionStore>();
            await StartAsyncTaskWithPerfAnalysis("Init type manager", () => scheduler
                .QueueTask(() => this.typeManager.Initialize(versionStore), this.typeManager.SchedulingContext)
                .WithTimeout(this.initTimeout, $"TypeManager Initializing failed due to timeout {initTimeout}"), stopWatch);

            //if running in multi cluster scenario, start the MultiClusterNetwork Oracle
            if (this.multiClusterOracle != null)
            {
                await StartAsyncTaskWithPerfAnalysis("Start multicluster oracle", StartMultiClusterOracle, stopWatch);
                async Task StartMultiClusterOracle()
                {
                    logger.Info("Starting multicluster oracle with my ServiceId={0} and ClusterId={1}.",
                        this.clusterOptions.ServiceId, this.clusterOptions.ClusterId);

                    this.multiClusterOracleContext = (multiClusterOracle as SystemTarget)?.SchedulingContext ??
                                                     this.fallbackScheduler.SchedulingContext;
                    await scheduler.QueueTask(() => multiClusterOracle.Start(), multiClusterOracleContext)
                        .WithTimeout(initTimeout, $"Starting MultiClusterOracle failed due to timeout {initTimeout}");
                    logger.Debug("multicluster oracle created successfully.");
                }
            }

            try
            {
                SiloStatisticsOptions statisticsOptions = Services.GetRequiredService<IOptions<SiloStatisticsOptions>>().Value;
                StartTaskWithPerfAnalysis("Start silo statistics", () => this.siloStatistics.Start(statisticsOptions), stopWatch);
                logger.Debug("Silo statistics manager started successfully.");

                // Finally, initialize the deployment load collector, for grains with load-based placement
                await StartAsyncTaskWithPerfAnalysis("Start deployment load collector", StartDeploymentLoadCollector, stopWatch);
                async Task StartDeploymentLoadCollector()
                {
                    var deploymentLoadPublisher = Services.GetRequiredService<DeploymentLoadPublisher>();
                    await this.scheduler.QueueTask(deploymentLoadPublisher.Start, deploymentLoadPublisher.SchedulingContext)
                        .WithTimeout(this.initTimeout, $"Starting DeploymentLoadPublisher failed due to timeout {initTimeout}");
                    logger.Debug("Silo deployment load publisher started successfully.");
                }


                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                this.platformWatchdog = new Watchdog(statisticsOptions.LogWriteInterval, this.healthCheckParticipants, this.executorService, this.loggerFactory);
                this.platformWatchdog.Start();
                if (this.logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Silo platform watchdog started successfully."); }
            }
            catch (Exception exc)
            {
                this.SafeExecute(() => this.logger.Error(ErrorCode.Runtime_Error_100330, String.Format("Error starting silo {0}. Going to FastKill().", this.SiloAddress), exc));
                throw;
            }
            if (logger.IsEnabled(LogLevel.Debug)) { logger.Debug("Silo.Start complete: System status = {0}", this.SystemStatus); }
        }

        private async Task OnBecomeActiveStart(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();
            StartTaskWithPerfAnalysis("Start gateway", StartGateway, stopWatch);
            void StartGateway()
            {
                // Now that we're active, we can start the gateway
                var mc = this.messageCenter as MessageCenter;
                mc?.StartGateway(this.Services.GetRequiredService<ClientObserverRegistrar>());
                logger.Debug("Message gateway service started successfully.");
            }

            await StartAsyncTaskWithPerfAnalysis("Starting local silo status oracle", BecomeActive, stopWatch);
            async Task BecomeActive()
            {
                await scheduler.QueueTask(this.membershipOracle.BecomeActive, this.membershipOracleContext)
                .WithTimeout(initTimeout, $"MembershipOracle activating failed due to timeout {initTimeout}");
                logger.Debug("Local silo status oracle became active successfully.");
            }
            this.SystemStatus = SystemStatus.Running;
        }

        private async Task OnActiveStart(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();
            if (this.reminderService != null)
            {
                await StartAsyncTaskWithPerfAnalysis("Start reminder service", StartReminderService, stopWatch);
                async Task StartReminderService()
                {
                    // so, we have the view of the membership in the consistentRingProvider. We can start the reminder service
                    this.reminderServiceContext = (this.reminderService as SystemTarget)?.SchedulingContext ??
                                                  this.fallbackScheduler.SchedulingContext;
                    await this.scheduler.QueueTask(this.reminderService.Start, this.reminderServiceContext)
                        .WithTimeout(this.initTimeout, $"Starting ReminderService failed due to timeout {initTimeout}");
                    this.logger.Debug("Reminder service started successfully.");
                }
            }
            foreach (var grainService in grainServices)
            {
                await StartGrainService(grainService);
            }
        }

        private async Task CreateGrainServices()
        {
            var grainServices = this.Services.GetServices<IGrainService>();
            foreach (var grainService in grainServices)
            {
                await RegisterGrainService(grainService);

            }
        }

        private async Task RegisterGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;
            RegisterSystemTarget(grainService);
            grainServices.Add(grainService);

            await this.scheduler.QueueTask(() => grainService.Init(Services), grainService.SchedulingContext).WithTimeout(this.initTimeout, $"GrainService Initializing failed due to timeout {initTimeout}");
            logger.Info($"Grain Service {service.GetType().FullName} registered successfully.");
        }

        private async Task StartGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;

            await this.scheduler.QueueTask(grainService.Start, grainService.SchedulingContext).WithTimeout(this.initTimeout, $"Starting GrainService failed due to timeout {initTimeout}");
            logger.Info($"Grain Service {service.GetType().FullName} started successfully.");
        }

        private void ConfigureThreadPoolAndServicePointSettings()
        {
            PerformanceTuningOptions performanceTuningOptions = Services.GetRequiredService<IOptions<PerformanceTuningOptions>>().Value;
            if (performanceTuningOptions.MinDotNetThreadPoolSize > 0)
            {
                int workerThreads;
                int completionPortThreads;
                ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
                if (performanceTuningOptions.MinDotNetThreadPoolSize > workerThreads ||
                    performanceTuningOptions.MinDotNetThreadPoolSize > completionPortThreads)
                {
                    // if at least one of the new values is larger, set the new min values to be the larger of the prev. and new config value.
                    int newWorkerThreads = Math.Max(performanceTuningOptions.MinDotNetThreadPoolSize, workerThreads);
                    int newCompletionPortThreads = Math.Max(performanceTuningOptions.MinDotNetThreadPoolSize, completionPortThreads);
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
                performanceTuningOptions.Expect100Continue, performanceTuningOptions.DefaultConnectionLimit, performanceTuningOptions.UseNagleAlgorithm);
            ServicePointManager.Expect100Continue = performanceTuningOptions.Expect100Continue;
            ServicePointManager.DefaultConnectionLimit = performanceTuningOptions.DefaultConnectionLimit;
            ServicePointManager.UseNagleAlgorithm = performanceTuningOptions.UseNagleAlgorithm;
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
        public async Task StopAsync(CancellationToken cancellationToken)
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

                await this.siloTerminatedTask.Task;
                return;
            }

            try
            {
                await this.scheduler.QueueTask(() => this.siloLifecycle.OnStop(cancellationToken), this.lifecycleSchedulingSystemTarget.SchedulingContext);
            } finally
            {
                SafeExecute(scheduler.Stop);
                SafeExecute(scheduler.PrintStatistics);
            }
        }

        private Task OnRuntimeServicesStop(CancellationToken cancellationToken)
        {
            // Start rejecting all silo to silo application messages
            SafeExecute(messageCenter.BlockApplicationMessages);

            // Stop scheduling/executing application turns
            SafeExecute(scheduler.StopApplicationTurns);

            // Directory: Speed up directory handoff
            // will be started automatically when directory receives SiloStatusChangeNotification(Stopping)

            SafeExecute(() => LocalGrainDirectory.StopPreparationCompletion.WaitWithThrow(stopTimeout));

            return Task.CompletedTask;
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

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            bool gracefully = !ct.IsCancellationRequested;
            string operation = gracefully ? "Shutdown()" : "Stop()";
            try
            {
                if (gracefully)
                {
                    logger.Info(ErrorCode.SiloShuttingDown, "Silo starting to Shutdown()");
                    // Write "ShutDown" state in the table + broadcast gossip msgs to re-read the table to everyone
                    await scheduler.QueueTask(this.membershipOracle.ShutDown, this.membershipOracleContext)
                        .WithTimeout(stopTimeout, $"MembershipOracle Shutting down failed due to timeout {stopTimeout}");
                    // Deactivate all grains
                    SafeExecute(() => catalog.DeactivateAllActivations().WaitWithThrow(stopTimeout));
                }
                else
                {
                    logger.Info(ErrorCode.SiloStopping, "Silo starting to Stop()");
                    // Write "Stopping" state in the table + broadcast gossip msgs to re-read the table to everyone
                    await scheduler.QueueTask(this.membershipOracle.Stop, this.membershipOracleContext)
                        .WithTimeout(stopTimeout, $"Stopping MembershipOracle faield due to timeout {stopTimeout}");
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.SiloFailedToStopMembership, String.Format("Failed to {0} membership oracle. About to FastKill this silo.", operation), exc);
                return; // will go to finally
            }
            // Stop the gateway
            SafeExecute(messageCenter.StopAcceptingClientMessages);
        }

        private async Task OnActiveStop(CancellationToken ct)
        {
            if (reminderService != null)
            {
                // 2: Stop reminder service
                await scheduler.QueueTask(reminderService.Stop, this.reminderServiceContext)
                    .WithTimeout(stopTimeout, $"Stopping ReminderService failed due to timeout {stopTimeout}");
            }
            foreach (var grainService in grainServices)
            {
                await this.scheduler.QueueTask(grainService.Stop, grainService.SchedulingContext).WithTimeout(this.stopTimeout, $"Stopping GrainService failed due to timeout {initTimeout}");
                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Debug(String.Format("{0} Grain Service with Id {1} stopped successfully.", grainService.GetType().FullName, grainService.GetPrimaryKeyLong(out string ignored)));
                }
            }
        }

        private void SafeExecute(Action action)
        {
            Utils.SafeExecute(action, logger, "Silo.Stop");
        }

        private void HandleProcessExit(object sender, EventArgs e)
        {
            // NOTE: We need to minimize the amount of processing occurring on this code path -- we only have under approx 2-3 seconds before process exit will occur
            this.logger.Warn(ErrorCode.Runtime_Error_100220, "Process is exiting");
            this.Stop();
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
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeInitialize, (ct) => Task.Run(() => OnRuntimeInitializeStart(ct)), (ct) => Task.Run(() => OnRuntimeInitializeStop(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeServices, (ct) => Task.Run(() => OnRuntimeServicesStart(ct)), (ct) => Task.Run(() => OnRuntimeServicesStop(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeGrainServices, (ct) => Task.Run(() => OnRuntimeGrainServicesStart(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.BecomeActive, (ct) => Task.Run(() => OnBecomeActiveStart(ct)), (ct) => Task.Run(() => OnBecomeActiveStop(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.Active, (ct) => Task.Run(() => OnActiveStart(ct)), (ct) => Task.Run(() => OnActiveStop(ct)));
        }
    }

    // A dummy system target for fallback scheduler
    internal class FallbackSystemTarget : SystemTarget
    {
        public FallbackSystemTarget(ILocalSiloDetails localSiloDetails, ILoggerFactory loggerFactory)
            : base(Constants.FallbackSystemTargetId, localSiloDetails.SiloAddress, loggerFactory)
        {
        }
    }

    // A dummy system target for fallback scheduler
    internal class LifecycleSchedulingSystemTarget : SystemTarget
    {
        public LifecycleSchedulingSystemTarget(ILocalSiloDetails localSiloDetails, ILoggerFactory loggerFactory)
            : base(Constants.LifecycleSchedulingSystemTargetId, localSiloDetails.SiloAddress, loggerFactory)
        {
        }
    }
}

