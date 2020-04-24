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
using Orleans.Runtime.Providers;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Scheduler;
using Orleans.Services;
using Orleans.Streams;
using Orleans.Runtime.Versions;
using Orleans.Versions;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Internal;

namespace Orleans.Runtime
{
    /// <summary>
    /// Orleans silo.
    /// </summary>
    public class Silo
    {
        /// <summary> Standard name for Primary silo. </summary>
        public const string PrimarySiloName = "Primary";
        private static TimeSpan WaitForMessageToBeQueuedForOutbound = TimeSpan.FromSeconds(2);
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
        private readonly MessageCenter messageCenter;
        private readonly OrleansTaskScheduler scheduler;
        private readonly LocalGrainDirectory localGrainDirectory;
        private readonly ActivationDirectory activationDirectory;
        private readonly ILogger logger;
        private TypeManager typeManager;
        private readonly TaskCompletionSource<int> siloTerminatedTask =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SiloStatisticsManager siloStatistics;
        private readonly InsideRuntimeClient runtimeClient;
        private IReminderService reminderService;
        private SystemTarget fallbackScheduler;
        private readonly ISiloStatusOracle siloStatusOracle;
        private Watchdog platformWatchdog;
        private readonly TimeSpan initTimeout;
        private readonly TimeSpan stopTimeout = TimeSpan.FromMinutes(1);
        private readonly Catalog catalog;
        private readonly object lockable = new object();
        private readonly GrainFactory grainFactory;
        private readonly ISiloLifecycleSubject siloLifecycle;
        private readonly IMembershipService membershipService;
        private List<GrainService> grainServices = new List<GrainService>();

        private readonly ILoggerFactory loggerFactory;
        /// <summary>
        /// Gets the type of this 
        /// </summary>
        internal string Name => this.siloDetails.Name;
        internal OrleansTaskScheduler LocalScheduler { get { return scheduler; } }
        internal ILocalGrainDirectory LocalGrainDirectory { get { return localGrainDirectory; } }
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

        private bool isFastKilledNeeded = false; // Set to true if something goes wrong in the shutdown/stop phase

        private IGrainContext reminderServiceContext;
        private LifecycleSchedulingSystemTarget lifecycleSchedulingSystemTarget;

        /// <summary>
        /// Initializes a new instance of the <see cref="Silo"/> class.
        /// </summary>
        /// <param name="siloDetails">The silo initialization parameters</param>
        /// <param name="services">Dependency Injection container</param>
        [Obsolete("This constructor is obsolete and may be removed in a future release. Use SiloHostBuilder to create an instance of ISiloHost instead.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Should not Dispose of messageCenter in this method because it continues to run / exist after this point.")]
        public Silo(ILocalSiloDetails siloDetails, IServiceProvider services)
        {
            string name = siloDetails.Name;
            // Temporarily still require this. Hopefuly gone when 2.0 is released.
            this.siloDetails = siloDetails;
            this.SystemStatus = SystemStatus.Creating;

            var startTime = DateTime.UtcNow;

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

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var highestLogLevel = logger.IsEnabled(LogLevel.Trace) ? nameof(LogLevel.Trace) : nameof(LogLevel.Debug);
                logger.LogWarning(
                    new EventId((int)ErrorCode.SiloGcWarning),
                    $"A verbose logging level ({highestLogLevel}) is configured. This will impact performance. The recommended log level is {nameof(LogLevel.Information)}.");
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

            // Now the incoming message agents
            var messageFactory = this.Services.GetRequiredService<MessageFactory>();
            var messagingTrace = this.Services.GetRequiredService<MessagingTrace>();
            messageCenter.RegisterLocalMessageHandler(new IncomingMessageHandler(
                messageCenter,
                activationDirectory,
                scheduler,
                catalog.Dispatcher,
                messageFactory,
                this.loggerFactory.CreateLogger<IncomingMessageHandler>(),
                messagingTrace));

            siloStatusOracle = Services.GetRequiredService<ISiloStatusOracle>();
            this.membershipService = Services.GetRequiredService<IMembershipService>();
            this.clusterOptions = Services.GetRequiredService<IOptions<ClusterOptions>>().Value;

            this.SystemStatus = SystemStatus.Created;

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

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // SystemTarget for provider init calls
            this.lifecycleSchedulingSystemTarget = Services.GetRequiredService<LifecycleSchedulingSystemTarget>();
            this.fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(lifecycleSchedulingSystemTarget);

            try
            {
                await this.scheduler.QueueTask(() => this.siloLifecycle.OnStart(cancellationToken), this.lifecycleSchedulingSystemTarget);
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

            logger.Debug("Creating {0} System Target", "DeploymentLoadPublisher");
            RegisterSystemTarget(Services.GetRequiredService<DeploymentLoadPublisher>());
            
            logger.Debug("Creating {0} System Target", "RemoteGrainDirectory + CacheValidator");
            RegisterSystemTarget(LocalGrainDirectory.RemoteGrainDirectory);
            RegisterSystemTarget(LocalGrainDirectory.CacheValidator);

            logger.Debug("Creating {0} System Target", "RemoteClusterGrainDirectory");

            logger.Debug("Creating {0} System Target", "ClientObserverRegistrar + TypeManager");

            this.RegisterSystemTarget(this.Services.GetRequiredService<ClientObserverRegistrar>());
            var implicitStreamSubscriberTable = Services.GetRequiredService<ImplicitStreamSubscriberTable>();
            var versionDirectorManager = this.Services.GetRequiredService<CachedVersionSelectorManager>();
            var grainTypeManager = this.Services.GetRequiredService<GrainTypeManager>();
            IOptions<TypeManagementOptions> typeManagementOptions = this.Services.GetRequiredService<IOptions<TypeManagementOptions>>();
            typeManager = new TypeManager(SiloAddress, grainTypeManager, siloStatusOracle, LocalScheduler, typeManagementOptions.Value.TypeMapRefreshInterval, implicitStreamSubscriberTable, this.grainFactory, versionDirectorManager,
                this.loggerFactory);
            this.RegisterSystemTarget(typeManager);

            logger.Debug("Creating {0} System Target", "MembershipOracle");
            if (this.membershipService is SystemTarget)
            {
                RegisterSystemTarget((SystemTarget)this.membershipService);
            }

            logger.Debug("Finished creating System Targets for this silo.");
        }

        private async Task InjectDependencies()
        {
            catalog.SiloStatusOracle = this.siloStatusOracle;
            this.siloStatusOracle.SubscribeToSiloStatusEvents(localGrainDirectory);

            // consistentRingProvider is not a system target per say, but it behaves like the localGrainDirectory, so it is here
            this.siloStatusOracle.SubscribeToSiloStatusEvents((ISiloStatusListener)RingProvider);

            this.siloStatusOracle.SubscribeToSiloStatusEvents(typeManager);

            this.siloStatusOracle.SubscribeToSiloStatusEvents(Services.GetRequiredService<DeploymentLoadPublisher>());

            var reminderTable = Services.GetService<IReminderTable>();
            if (reminderTable != null)
            {
                logger.Info($"Creating reminder grain service for type={reminderTable.GetType()}");

                // Start the reminder service system target
                var timerFactory = this.Services.GetRequiredService<IAsyncTimerFactory>();
                reminderService = new LocalReminderService(this, reminderTable, this.initTimeout, this.loggerFactory, timerFactory);
                RegisterSystemTarget((SystemTarget)reminderService);
            }

            RegisterSystemTarget(catalog);
            await scheduler.QueueActionAsync(catalog.Start, catalog)
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

            StartTaskWithPerfAnalysis("Start local grain directory", LocalGrainDirectory.Start, stopWatch);

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

            // Load and init grain services before silo becomes active.
            await StartAsyncTaskWithPerfAnalysis("Init grain services",
                () => CreateGrainServices(), stopWatch);
            
            var versionStore = Services.GetService<IVersionStore>();
            await StartAsyncTaskWithPerfAnalysis("Init type manager", () => scheduler
                .QueueTask(() => this.typeManager.Initialize(versionStore), this.typeManager)
                .WithTimeout(this.initTimeout, $"TypeManager Initializing failed due to timeout {initTimeout}"), stopWatch);

            try
            {
                StatisticsOptions statisticsOptions = Services.GetRequiredService<IOptions<StatisticsOptions>>().Value;
                StartTaskWithPerfAnalysis("Start silo statistics", () => this.siloStatistics.Start(statisticsOptions), stopWatch);
                logger.Debug("Silo statistics manager started successfully.");

                // Finally, initialize the deployment load collector, for grains with load-based placement
                await StartAsyncTaskWithPerfAnalysis("Start deployment load collector", StartDeploymentLoadCollector, stopWatch);
                async Task StartDeploymentLoadCollector()
                {
                    var deploymentLoadPublisher = Services.GetRequiredService<DeploymentLoadPublisher>();
                    await this.scheduler.QueueTask(deploymentLoadPublisher.Start, deploymentLoadPublisher)
                        .WithTimeout(this.initTimeout, $"Starting DeploymentLoadPublisher failed due to timeout {initTimeout}");
                    logger.Debug("Silo deployment load publisher started successfully.");
                }


                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                var healthCheckParticipants = this.Services.GetService<IEnumerable<IHealthCheckParticipant>>().ToList();
                this.platformWatchdog = new Watchdog(statisticsOptions.LogWriteInterval, healthCheckParticipants, this.loggerFactory.CreateLogger<Watchdog>());
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

        private Task OnBecomeActiveStart(CancellationToken ct)
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

            this.SystemStatus = SystemStatus.Running;
            return Task.CompletedTask;
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
                    this.reminderServiceContext = (this.reminderService as IGrainContext) ?? this.fallbackScheduler;
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

            await this.scheduler.QueueTask(() => grainService.Init(Services), grainService).WithTimeout(this.initTimeout, $"GrainService Initializing failed due to timeout {initTimeout}");
            logger.Info($"Grain Service {service.GetType().FullName} registered successfully.");
        }

        private async Task StartGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;

            await this.scheduler.QueueTask(grainService.Start, grainService).WithTimeout(this.initTimeout, $"Starting GrainService failed due to timeout {initTimeout}");
            logger.Info($"Grain Service {service.GetType().FullName} started successfully.");
        }

        private void ConfigureThreadPoolAndServicePointSettings()
        {
            PerformanceTuningOptions performanceTuningOptions = Services.GetRequiredService<IOptions<PerformanceTuningOptions>>().Value;
            if (performanceTuningOptions.MinDotNetThreadPoolSize > 0 || performanceTuningOptions.MinIOThreadPoolSize > 0)
            {
                int workerThreads;
                int completionPortThreads;
                ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
                if (performanceTuningOptions.MinDotNetThreadPoolSize > workerThreads ||
                    performanceTuningOptions.MinIOThreadPoolSize > completionPortThreads)
                {
                    // if at least one of the new values is larger, set the new min values to be the larger of the prev. and new config value.
                    int newWorkerThreads = Math.Max(performanceTuningOptions.MinDotNetThreadPoolSize, workerThreads);
                    int newCompletionPortThreads = Math.Max(performanceTuningOptions.MinIOThreadPoolSize, completionPortThreads);
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
            var cancellationSource = new CancellationTokenSource(this.stopTimeout);
            StopAsync(cancellationSource.Token).GetAwaiter().GetResult();
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
                    await Task.Delay(pause);
                }

                await this.SiloTerminated;
                return;
            }

            try
            {
                await this.scheduler.QueueTask(() => this.siloLifecycle.OnStop(cancellationToken), this.lifecycleSchedulingSystemTarget);
            }
            finally
            {
                SafeExecute(scheduler.Stop);
                SafeExecute(scheduler.PrintStatistics);
            }
        }

        private Task OnRuntimeServicesStop(CancellationToken ct)
        {
            if (this.isFastKilledNeeded || ct.IsCancellationRequested) // No time for this
                return Task.CompletedTask;

            // Start rejecting all silo to silo application messages
            SafeExecute(messageCenter.BlockApplicationMessages);

            // Stop scheduling/executing application turns
            SafeExecute(scheduler.StopApplicationTurns);

            return Task.CompletedTask;
        }

        private Task OnRuntimeInitializeStop(CancellationToken ct)
        {
            // 10, 11, 12: Write Dead in the table, Drain scheduler, Stop msg center, ...
            logger.Info(ErrorCode.SiloStopped, "Silo is Stopped()");

            // timers
            if (platformWatchdog != null) 
                SafeExecute(platformWatchdog.Stop); // Silo may be dying before platformWatchdog was set up

            if (!ct.IsCancellationRequested)
                SafeExecute(activationDirectory.PrintActivationDirectory);

            SafeExecute(messageCenter.Stop);
            SafeExecute(siloStatistics.Stop);

            SafeExecute(() => this.SystemStatus = SystemStatus.Terminated);

            // Setting the event should be the last thing we do.
            // Do nothing after that!
            this.siloTerminatedTask.SetResult(0);
            return Task.CompletedTask;
        }

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            if (this.isFastKilledNeeded)
                return;

            bool gracefully = !ct.IsCancellationRequested;
            string operation = gracefully ? "Shutdown()" : "Stop()";
            try
            {
                if (gracefully)
                {
                    logger.Info(ErrorCode.SiloShuttingDown, "Silo starting to Shutdown()");

                    //Stop LocalGrainDirectory
                    await scheduler.QueueTask(()=>localGrainDirectory.Stop(true), localGrainDirectory.CacheValidator)
                        .WithCancellation(ct, "localGrainDirectory Stop failed because the task was cancelled");
                    SafeExecute(() => catalog.DeactivateAllActivations().Wait(ct));
                    //wait for all queued message sent to OutboundMessageQueue before MessageCenter stop and OutboundMessageQueue stop. 
                    await Task.Delay(WaitForMessageToBeQueuedForOutbound);
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.SiloFailedToStopMembership,
                    $"Failed to {operation}. About to FastKill this silo.", exc);
                this.isFastKilledNeeded = true;
            }

            // Stop the gateway
            SafeExecute(messageCenter.StopAcceptingClientMessages);

            SafeExecute(() => catalog?.Stop());
        }

        private async Task OnActiveStop(CancellationToken ct)
        {
            if (this.isFastKilledNeeded || ct.IsCancellationRequested)
                return;

            if (reminderService != null)
            {
                await this.scheduler
                    .QueueTask(reminderService.Stop, this.reminderServiceContext)
                    .WithCancellation(ct, "Stopping ReminderService failed because the task was cancelled");
            }

            foreach (var grainService in grainServices)
            {
                await this.scheduler
                    .QueueTask(grainService.Stop, grainService)
                    .WithCancellation(ct, "Stopping GrainService failed because the task was cancelled");

                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Debug(
                        "{GrainServiceType} Grain Service with Id {GrainServiceId} stopped successfully.", 
                        grainService.GetType().FullName, 
                        grainService.GetPrimaryKeyLong(out string ignored));
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
            this.isFastKilledNeeded = true;
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
                    var workItemGroup = scheduler.GetWorkItemGroup(activationData);
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

