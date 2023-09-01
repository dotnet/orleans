using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;
using Orleans.Services;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime
{
    /// <summary>
    /// Orleans silo.
    /// </summary>
    public class Silo
    {
        /// <summary>Standard name for Primary silo. </summary>
        public const string PrimarySiloName = "Primary";
        private readonly ILocalSiloDetails siloDetails;
        private readonly MessageCenter messageCenter;
        private readonly LocalGrainDirectory localGrainDirectory;
        private readonly ILogger logger;
        private readonly TaskCompletionSource<int> siloTerminatedTask = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InsideRuntimeClient runtimeClient;
        private SystemTarget fallbackScheduler;
        private readonly ISiloStatusOracle siloStatusOracle;
        private Watchdog platformWatchdog;
        private readonly TimeSpan waitForMessageToBeQueuedForOutbound;
        private readonly TimeSpan initTimeout;
        private readonly TimeSpan stopTimeout = TimeSpan.FromMinutes(1);
        private readonly Catalog catalog;
        private readonly object lockable = new object();
        private readonly GrainFactory grainFactory;
        private readonly ISiloLifecycleSubject siloLifecycle;
        private readonly IMembershipService membershipService;
        internal List<GrainService> grainServices = new List<GrainService>();

        private readonly ILoggerFactory loggerFactory;
        /// <summary>
        /// Gets the type of this
        /// </summary>
        internal string Name => this.siloDetails.Name;
        internal ILocalGrainDirectory LocalGrainDirectory { get { return localGrainDirectory; } }
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal List<GrainService> GrainServices => grainServices;

        internal SystemStatus SystemStatus { get; set; }

        internal IServiceProvider Services { get; }

        /// <summary>Gets the address of this silo.</summary>
        public SiloAddress SiloAddress => this.siloDetails.SiloAddress;

        /// <summary>
        /// Gets a <see cref="Task"/> which completes once the silo has terminated.
        /// </summary>
        public Task SiloTerminated { get { return this.siloTerminatedTask.Task; } } // one event for all types of termination (shutdown, stop and fast kill).

        private bool isFastKilledNeeded = false; // Set to true if something goes wrong in the shutdown/stop phase

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

            IOptions<ClusterMembershipOptions> clusterMembershipOptions = services.GetRequiredService<IOptions<ClusterMembershipOptions>>();
            initTimeout = clusterMembershipOptions.Value.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), clusterMembershipOptions.Value.MaxJoinAttemptTime);
                stopTimeout = initTimeout;
            }

            var localEndpoint = this.siloDetails.SiloAddress.Endpoint;

            this.Services = services;

            //set PropagateActivityId flag from node config
            IOptions<SiloMessagingOptions> messagingOptions = services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            this.waitForMessageToBeQueuedForOutbound = messagingOptions.Value.WaitForMessageToBeQueuedForOutboundTime;

            this.loggerFactory = this.Services.GetRequiredService<ILoggerFactory>();
            logger = this.loggerFactory.CreateLogger<Silo>();

            logger.LogInformation(
                (int)ErrorCode.SiloGcSetting,
                "Silo starting with GC settings: ServerGC={ServerGC} GCLatencyMode={GCLatencyMode}",
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString());
            if (!GCSettings.IsServerGC)
            {
                logger.LogWarning((int)ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\">");
                logger.LogWarning((int)ErrorCode.SiloGcWarning, "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines).");
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var highestLogLevel = logger.IsEnabled(LogLevel.Trace) ? nameof(LogLevel.Trace) : nameof(LogLevel.Debug);
                logger.LogWarning(
                    (int)ErrorCode.SiloGcWarning,
                    $"A verbose logging level ({{highestLogLevel}}) is configured. This will impact performance. The recommended log level is {nameof(LogLevel.Information)}.",
                    highestLogLevel);
            }

            logger.LogInformation(
                (int)ErrorCode.SiloInitializing,
                "-------------- Initializing silo on host {HostName} MachineName {MachineNAme} at {LocalEndpoint}, gen {Generation} --------------",
                this.siloDetails.DnsHostName,
                Environment.MachineName,
                localEndpoint,
                this.siloDetails.SiloAddress.Generation);
            logger.LogInformation(
                (int)ErrorCode.SiloInitConfig,
                "Starting silo {SiloName}",
                name);

            try
            {
                grainFactory = Services.GetRequiredService<GrainFactory>();
            }
            catch (InvalidOperationException exc)
            {
                logger.LogError(
                    (int)ErrorCode.SiloStartError, exc, "Exception during Silo.Start, GrainFactory was not registered in Dependency Injection container");
                throw;
            }

            runtimeClient = Services.GetRequiredService<InsideRuntimeClient>();

            // Initialize the message center
            messageCenter = Services.GetRequiredService<MessageCenter>();
            messageCenter.SniffIncomingMessage = runtimeClient.SniffIncomingMessage;

            // Now the router/directory service
            // This has to come after the message center //; note that it then gets injected back into the message center.;
            localGrainDirectory = Services.GetRequiredService<LocalGrainDirectory>();

            // Now the consistent ring provider
            RingProvider = Services.GetRequiredService<IConsistentRingProvider>();

            catalog = Services.GetRequiredService<Catalog>();

            siloStatusOracle = Services.GetRequiredService<ISiloStatusOracle>();
            this.membershipService = Services.GetRequiredService<IMembershipService>();

            this.SystemStatus = SystemStatus.Created;

            this.siloLifecycle = this.Services.GetRequiredService<ISiloLifecycleSubject>();
            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<ISiloLifecycle>> lifecycleParticipants = this.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach(ILifecycleParticipant<ISiloLifecycle> participant in lifecycleParticipants)
            {
                participant?.Participate(this.siloLifecycle);
            }

            // register all named lifecycle participants
            var namedLifecycleParticipantCollection = this.Services.GetService<IKeyedServiceCollection<string,ILifecycleParticipant<ISiloLifecycle>>>();
            if (namedLifecycleParticipantCollection?.GetServices(Services)?.Select(s => s.GetService(Services)) is { } namedParticipants)
            {
                foreach (ILifecycleParticipant<ISiloLifecycle> participant in namedParticipants)
                {
                    participant.Participate(this.siloLifecycle);
                }
            }

            // add self to lifecycle
            this.Participate(this.siloLifecycle);

            logger.LogInformation(
                (int)ErrorCode.SiloInitializingFinished,
                "-------------- Started silo {SiloAddress}, ConsistentHashCode {HashCode} --------------",
                SiloAddress.ToString(),
                SiloAddress.GetConsistentHashCode().ToString("X"));
        }

        /// <summary>
        /// Starts the silo.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token which can be used to cancel the operation.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // SystemTarget for provider init calls
            this.lifecycleSchedulingSystemTarget = Services.GetRequiredService<LifecycleSchedulingSystemTarget>();
            this.fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(lifecycleSchedulingSystemTarget);

            try
            {
                await this.lifecycleSchedulingSystemTarget.WorkItemGroup.QueueTask(() => this.siloLifecycle.OnStart(cancellationToken), lifecycleSchedulingSystemTarget);
            }
            catch (Exception exc)
            {
                logger.LogError((int)ErrorCode.SiloStartError, exc, "Exception during Silo.Start");
                throw;
            }
        }

        private void CreateSystemTargets()
        {
            var siloControl = ActivatorUtilities.CreateInstance<SiloControl>(Services);
            RegisterSystemTarget(siloControl);

            RegisterSystemTarget(Services.GetRequiredService<DeploymentLoadPublisher>());
            RegisterSystemTarget(LocalGrainDirectory.RemoteGrainDirectory);
            RegisterSystemTarget(LocalGrainDirectory.CacheValidator);

            this.RegisterSystemTarget(this.Services.GetRequiredService<ClientDirectory>());

            if (this.membershipService is SystemTarget)
            {
                RegisterSystemTarget((SystemTarget)this.membershipService);
            }
        }

        private void InjectDependencies()
        {
            catalog.SiloStatusOracle = this.siloStatusOracle;
            this.siloStatusOracle.SubscribeToSiloStatusEvents(localGrainDirectory);

            // consistentRingProvider is not a system target per say, but it behaves like the localGrainDirectory, so it is here
            this.siloStatusOracle.SubscribeToSiloStatusEvents((ISiloStatusListener)RingProvider);

            this.siloStatusOracle.SubscribeToSiloStatusEvents(Services.GetRequiredService<DeploymentLoadPublisher>());

            // SystemTarget for provider init calls
            this.fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(fallbackScheduler);
        }

        private Task OnRuntimeInitializeStart(CancellationToken ct)
        {
            lock (lockable)
            {
                if (!this.SystemStatus.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(string.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", this.SystemStatus));

                this.SystemStatus = SystemStatus.Starting;
            }

            logger.LogInformation((int)ErrorCode.SiloStarting, "Silo Start()");
            return Task.CompletedTask;
        }

        private void StartTaskWithPerfAnalysis(string taskName, Action task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            task.Invoke();
            stopWatch.Stop();
            this.logger.LogInformation(
                (int)ErrorCode.SiloStartPerfMeasure,
                "{TaskName} took {ElapsedMilliseconds} milliseconds to finish",
                taskName,
                stopWatch.ElapsedMilliseconds);
        }

        private async Task StartAsyncTaskWithPerfAnalysis(string taskName, Func<Task> task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            await task.Invoke();
            stopWatch.Stop();
            this.logger.LogInformation(
                (int)ErrorCode.SiloStartPerfMeasure,
                "{TaskName} took {ElapsedMilliseconds} milliseconds to finish",
                taskName,
                stopWatch.ElapsedMilliseconds);
        }

        private Task OnRuntimeServicesStart(CancellationToken ct)
        {
            //TODO: Setup all (or as many as possible) of the class started in this call to work directly with lifecyce
            var stopWatch = Stopwatch.StartNew();

            StartTaskWithPerfAnalysis("Start local grain directory", LocalGrainDirectory.Start, stopWatch);

            // This has to follow the above steps that start the runtime components
            CreateSystemTargets();
            InjectDependencies();

            return Task.CompletedTask;
        }

        private async Task OnRuntimeGrainServicesStart(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            // Load and init grain services before silo becomes active.
            await StartAsyncTaskWithPerfAnalysis("Init grain services",
                () => CreateGrainServices(), stopWatch);

            try
            {
                // Finally, initialize the deployment load collector, for grains with load-based placement
                await StartAsyncTaskWithPerfAnalysis("Start deployment load collector", StartDeploymentLoadCollector, stopWatch);
                async Task StartDeploymentLoadCollector()
                {
                    var deploymentLoadPublisher = Services.GetRequiredService<DeploymentLoadPublisher>();
                    await deploymentLoadPublisher.WorkItemGroup.QueueTask(deploymentLoadPublisher.Start, deploymentLoadPublisher)
                        .WithTimeout(this.initTimeout, $"Starting DeploymentLoadPublisher failed due to timeout {initTimeout}");
                    logger.LogDebug("Silo deployment load publisher started successfully.");
                }

                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                var healthCheckParticipants = this.Services.GetService<IEnumerable<IHealthCheckParticipant>>().ToList();
                var membershipOptions = Services.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
                this.platformWatchdog = new Watchdog(membershipOptions.LocalHealthDegradationMonitoringPeriod, healthCheckParticipants, this.loggerFactory.CreateLogger<Watchdog>());
                this.platformWatchdog.Start();
                if (this.logger.IsEnabled(LogLevel.Debug)) { logger.LogDebug("Silo platform watchdog started successfully."); }
            }
            catch (Exception exc)
            {
                this.logger.LogError(
                    (int)ErrorCode.Runtime_Error_100330,
                    exc,
                    "Error starting silo {SiloAddress}. Going to FastKill().",
                    this.SiloAddress);
                throw;
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Silo.Start complete: System status = {SystemStatus}", this.SystemStatus);
            }
        }

        private Task OnBecomeActiveStart(CancellationToken ct)
        {
            this.SystemStatus = SystemStatus.Running;
            return Task.CompletedTask;
        }

        private async Task OnActiveStart(CancellationToken ct)
        {
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

            await grainService.QueueTask(() => grainService.Init(Services)).WithTimeout(this.initTimeout, $"GrainService Initializing failed due to timeout {initTimeout}");
            logger.LogInformation(
                "Grain Service {GrainServiceType} registered successfully.",
                service.GetType().FullName);
        }

        private async Task StartGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;

            await grainService.QueueTask(grainService.Start).WithTimeout(this.initTimeout, $"Starting GrainService failed due to timeout {initTimeout}");
            logger.LogInformation("Grain Service {GrainServiceType} started successfully.",service.GetType().FullName);
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
        /// <param name="cancellationToken">
        /// A cancellation token which can be used to promptly terminate the silo.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            bool gracefully = !cancellationToken.IsCancellationRequested;
            if (gracefully)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug((int)ErrorCode.SiloShuttingDown, "Silo shutdown initiated (graceful)");
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning((int)ErrorCode.SiloShuttingDown, "Silo shutdown initiated (non-graceful)");
                }
            }

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
                    throw new InvalidOperationException($"Attempted to shutdown a silo which is not in the {nameof(SystemStatus.Running)} state. This silo is in the {this.SystemStatus} state.");
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
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug((int)ErrorCode.SiloStopInProgress, "Silo shutdown in progress. Waiting for shutdown to be completed.");
                }
                var pause = TimeSpan.FromSeconds(1);                

                while (!this.SystemStatus.Equals(SystemStatus.Terminated))
                {                    
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug((int)ErrorCode.WaitingForSiloStop, "Silo shutdown still in progress...");
                    }
                    await Task.Delay(pause).ConfigureAwait(false);
                }

                await this.SiloTerminated.ConfigureAwait(false);
                return;
            }

            try
            {
                await this.lifecycleSchedulingSystemTarget.QueueTask(() => this.siloLifecycle.OnStop(cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                // log final status                
                if (gracefully)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug((int)ErrorCode.SiloShutDown, "Silo shutdown completed (graceful)!");
                    }
                }
                else
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.LogWarning((int)ErrorCode.SiloShutDown, "Silo shutdown completed (non-graceful)!");
                    }
                }

                // signal to all awaiters that the silo has terminated.
                await Task.Run(() => this.siloTerminatedTask.TrySetResult(0)).ConfigureAwait(false);
            }
        }

        private Task OnRuntimeServicesStop(CancellationToken ct)
        {
            if (this.isFastKilledNeeded || ct.IsCancellationRequested) // No time for this
                return Task.CompletedTask;

            // Start rejecting all silo to silo application messages
            SafeExecute(messageCenter.BlockApplicationMessages);

            return Task.CompletedTask;
        }

        private async Task OnRuntimeInitializeStop(CancellationToken ct)
        {
            if (platformWatchdog != null)
            {
                SafeExecute(platformWatchdog.Stop); // Silo may be dying before platformWatchdog was set up
            }

            try
            {
                await messageCenter.StopAsync();
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, "Error stopping message center");
            }

            SystemStatus = SystemStatus.Terminated;
        }

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            if (this.isFastKilledNeeded)
                return;

            bool gracefully = !ct.IsCancellationRequested;
            try
            {
                if (gracefully)
                {
                    // Stop LocalGrainDirectory
                    var resolver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    localGrainDirectory.CacheValidator.WorkItemGroup.QueueAction(() =>
                    {
                        try
                        {
                            localGrainDirectory.Stop();
                            resolver.TrySetResult(true);
                        }
                        catch (Exception exc)
                        {
                            resolver.TrySetException(exc);
                        }
                    });
                    await resolver.Task;

                    try
                    {
                        await catalog.DeactivateAllActivations().WithCancellation(ct);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Error deactivating activations");
                    }

                    // Wait for all queued message sent to OutboundMessageQueue before MessageCenter stop and OutboundMessageQueue stop.
                    await Task.WhenAny(Task.Delay(waitForMessageToBeQueuedForOutbound), ct.WhenCancelled());
                }
            }
            catch (Exception exc)
            {
                logger.LogError(
                    (int)ErrorCode.SiloFailedToStopMembership,
                    exc,
                    "Failed to shutdown gracefully. About to terminate ungracefully");
                this.isFastKilledNeeded = true;
            }

            // Stop the gateway
            await messageCenter.StopAcceptingClientMessages();
        }

        private async Task OnActiveStop(CancellationToken ct)
        {
            if (this.isFastKilledNeeded || ct.IsCancellationRequested)
                return;

            if (this.messageCenter.Gateway != null)
            {
                await lifecycleSchedulingSystemTarget
                    .QueueTask(() => this.messageCenter.Gateway.SendStopSendMessages(this.grainFactory))
                    .WithCancellation("Sending gateway disconnection requests failed because the task was cancelled", ct);
            }

            foreach (var grainService in grainServices)
            {
                await grainService
                    .QueueTask(grainService.Stop)
                    .WithCancellation("Stopping GrainService failed because the task was cancelled", ct);

                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
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

        internal void RegisterSystemTarget(SystemTarget target) => this.catalog.RegisterSystemTarget(target);

        /// <inheritdoc/>
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
            : base(Constants.FallbackSystemTargetType, localSiloDetails.SiloAddress, loggerFactory)
        {
        }
    }

    // A dummy system target for fallback scheduler
    internal class LifecycleSchedulingSystemTarget : SystemTarget
    {
        public LifecycleSchedulingSystemTarget(ILocalSiloDetails localSiloDetails, ILoggerFactory loggerFactory)
            : base(Constants.LifecycleSchedulingSystemTargetType, localSiloDetails.SiloAddress, loggerFactory)
        {
        }
    }
}

