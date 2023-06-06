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
        internal string Name => siloDetails.Name;
        internal ILocalGrainDirectory LocalGrainDirectory => localGrainDirectory;
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal List<GrainService> GrainServices => grainServices;

        internal SystemStatus SystemStatus { get; set; }

        internal IServiceProvider Services { get; }

        /// <summary>Gets the address of this silo.</summary>
        public SiloAddress SiloAddress => siloDetails.SiloAddress;

        /// <summary>
        /// Gets a <see cref="Task"/> which completes once the silo has terminated.
        /// </summary>
        public Task SiloTerminated => siloTerminatedTask.Task;  // one event for all types of termination (shutdown, stop and fast kill).

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
            var name = siloDetails.Name;
            // Temporarily still require this. Hopefuly gone when 2.0 is released.
            this.siloDetails = siloDetails;
            SystemStatus = SystemStatus.Creating;

            var clusterMembershipOptions = services.GetRequiredService<IOptions<ClusterMembershipOptions>>();
            initTimeout = clusterMembershipOptions.Value.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), clusterMembershipOptions.Value.MaxJoinAttemptTime);
                stopTimeout = initTimeout;
            }

            var localEndpoint = this.siloDetails.SiloAddress.Endpoint;

            Services = services;

            //set PropagateActivityId flag from node config
            var messagingOptions = services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            waitForMessageToBeQueuedForOutbound = messagingOptions.Value.WaitForMessageToBeQueuedForOutboundTime;

            loggerFactory = Services.GetRequiredService<ILoggerFactory>();
            logger = loggerFactory.CreateLogger<Silo>();

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
            membershipService = Services.GetRequiredService<IMembershipService>();

            SystemStatus = SystemStatus.Created;

            siloLifecycle = Services.GetRequiredService<ISiloLifecycleSubject>();
            // register all lifecycle participants
            var lifecycleParticipants = Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach(var participant in lifecycleParticipants)
            {
                participant?.Participate(siloLifecycle);
            }

            // register all named lifecycle participants
            var namedLifecycleParticipantCollection = Services.GetService<IKeyedServiceCollection<string,ILifecycleParticipant<ISiloLifecycle>>>();
            if (namedLifecycleParticipantCollection?.GetServices(Services)?.Select(s => s.GetService(Services)) is { } namedParticipants)
            {
                foreach (var participant in namedParticipants)
                {
                    participant.Participate(siloLifecycle);
                }
            }

            // add self to lifecycle
            Participate(siloLifecycle);

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
            lifecycleSchedulingSystemTarget = Services.GetRequiredService<LifecycleSchedulingSystemTarget>();
            fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(lifecycleSchedulingSystemTarget);

            try
            {
                await lifecycleSchedulingSystemTarget.WorkItemGroup.QueueTask(() => siloLifecycle.OnStart(cancellationToken), lifecycleSchedulingSystemTarget);
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

            RegisterSystemTarget(Services.GetRequiredService<ClientDirectory>());

            if (membershipService is SystemTarget)
            {
                RegisterSystemTarget((SystemTarget)membershipService);
            }
        }

        private void InjectDependencies()
        {
            catalog.SiloStatusOracle = siloStatusOracle;
            siloStatusOracle.SubscribeToSiloStatusEvents(localGrainDirectory);

            // consistentRingProvider is not a system target per say, but it behaves like the localGrainDirectory, so it is here
            siloStatusOracle.SubscribeToSiloStatusEvents((ISiloStatusListener)RingProvider);

            siloStatusOracle.SubscribeToSiloStatusEvents(Services.GetRequiredService<DeploymentLoadPublisher>());

            // SystemTarget for provider init calls
            fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(fallbackScheduler);
        }

        private Task OnRuntimeInitializeStart(CancellationToken ct)
        {
            lock (lockable)
            {
                if (!SystemStatus.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(string.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", SystemStatus));

                SystemStatus = SystemStatus.Starting;
            }

            logger.LogInformation((int)ErrorCode.SiloStarting, "Silo Start()");
            return Task.CompletedTask;
        }

        private void StartTaskWithPerfAnalysis(string taskName, Action task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            task.Invoke();
            stopWatch.Stop();
            logger.LogInformation(
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
            logger.LogInformation(
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
                        .WithTimeout(initTimeout, $"Starting DeploymentLoadPublisher failed due to timeout {initTimeout}");
                    logger.LogDebug("Silo deployment load publisher started successfully.");
                }

                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                var healthCheckParticipants = Services.GetService<IEnumerable<IHealthCheckParticipant>>().ToList();
                var membershipOptions = Services.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
                platformWatchdog = new Watchdog(membershipOptions.LocalHealthDegradationMonitoringPeriod, healthCheckParticipants, loggerFactory.CreateLogger<Watchdog>());
                platformWatchdog.Start();
                if (logger.IsEnabled(LogLevel.Debug)) { logger.LogDebug("Silo platform watchdog started successfully."); }
            }
            catch (Exception exc)
            {
                logger.LogError(
                    (int)ErrorCode.Runtime_Error_100330,
                    exc,
                    "Error starting silo {SiloAddress}. Going to FastKill().",
                    SiloAddress);
                throw;
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Silo.Start complete: System status = {SystemStatus}", SystemStatus);
            }
        }

        private Task OnBecomeActiveStart(CancellationToken ct)
        {
            SystemStatus = SystemStatus.Running;
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
            var grainServices = Services.GetServices<IGrainService>();
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

            await grainService.QueueTask(() => grainService.Init(Services)).WithTimeout(initTimeout, $"GrainService Initializing failed due to timeout {initTimeout}");
            logger.LogInformation(
                "Grain Service {GrainServiceType} registered successfully.",
                service.GetType().FullName);
        }

        private async Task StartGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;

            await grainService.QueueTask(grainService.Start).WithTimeout(initTimeout, $"Starting GrainService failed due to timeout {initTimeout}");
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
            var cancellationSource = new CancellationTokenSource(stopTimeout);
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
            var gracefully = !cancellationToken.IsCancellationRequested;
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

            var stopAlreadyInProgress = false;
            lock (lockable)
            {
                if (SystemStatus.Equals(SystemStatus.Stopping) ||
                    SystemStatus.Equals(SystemStatus.ShuttingDown) ||
                    SystemStatus.Equals(SystemStatus.Terminated))
                {
                    stopAlreadyInProgress = true;
                    // Drop through to wait below
                }
                else if (!SystemStatus.Equals(SystemStatus.Running))
                {
                    throw new InvalidOperationException($"Attempted to shutdown a silo which is not in the {nameof(SystemStatus.Running)} state. This silo is in the {SystemStatus} state.");
                }
                else
                {
                    if (gracefully)
                        SystemStatus = SystemStatus.ShuttingDown;
                    else
                        SystemStatus = SystemStatus.Stopping;
                }
            }

            if (stopAlreadyInProgress)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug((int)ErrorCode.SiloStopInProgress, "Silo shutdown in progress. Waiting for shutdown to be completed.");
                }
                var pause = TimeSpan.FromSeconds(1);                

                while (!SystemStatus.Equals(SystemStatus.Terminated))
                {                    
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug((int)ErrorCode.WaitingForSiloStop, "Silo shutdown still in progress...");
                    }
                    await Task.Delay(pause).ConfigureAwait(false);
                }

                await SiloTerminated.ConfigureAwait(false);
                return;
            }

            try
            {
                await lifecycleSchedulingSystemTarget.QueueTask(() => siloLifecycle.OnStop(cancellationToken)).ConfigureAwait(false);
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
                await Task.Run(() => siloTerminatedTask.TrySetResult(0)).ConfigureAwait(false);
            }
        }

        private Task OnRuntimeServicesStop(CancellationToken ct)
        {
            if (isFastKilledNeeded || ct.IsCancellationRequested) // No time for this
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
                logger.LogError(exception, "Error stopping message center");
            }

            SystemStatus = SystemStatus.Terminated;
        }

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            if (isFastKilledNeeded)
                return;

            var gracefully = !ct.IsCancellationRequested;
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
                isFastKilledNeeded = true;
            }

            // Stop the gateway
            await messageCenter.StopAcceptingClientMessages();
        }

        private async Task OnActiveStop(CancellationToken ct)
        {
            if (isFastKilledNeeded || ct.IsCancellationRequested)
                return;

            if (messageCenter.Gateway != null)
            {
                await lifecycleSchedulingSystemTarget
                    .QueueTask(() => messageCenter.Gateway.SendStopSendMessages(grainFactory))
                    .WithCancellation(ct, "Sending gateway disconnection requests failed because the task was cancelled");
            }

            foreach (var grainService in grainServices)
            {
                await grainService
                    .QueueTask(grainService.Stop)
                    .WithCancellation(ct, "Stopping GrainService failed because the task was cancelled");

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "{GrainServiceType} Grain Service with Id {GrainServiceId} stopped successfully.",
                        grainService.GetType().FullName,
                        grainService.GetPrimaryKeyLong(out var ignored));
                }
            }
        }

        private void SafeExecute(Action action) => Utils.SafeExecute(action, logger, "Silo.Stop");

        internal void RegisterSystemTarget(SystemTarget target) => catalog.RegisterSystemTarget(target);

        /// <inheritdoc/>
        public override string ToString() => localGrainDirectory.ToString();

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

