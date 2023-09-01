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
        private readonly ILocalSiloDetails _siloDetails;
        private readonly MessageCenter _messageCenter;
        private readonly LocalGrainDirectory _localGrainDirectory;
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<int> _siloTerminatedTask = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InsideRuntimeClient _runtimeClient;
        private SystemTarget _fallbackScheduler;
        private readonly ISiloStatusOracle _siloStatusOracle;
        private Watchdog _platformWatchdog;
        private readonly TimeSpan _waitForMessageToBeQueuedForOutbound;
        private readonly TimeSpan _initTimeout;
        private readonly TimeSpan _stopTimeout = TimeSpan.FromMinutes(1);
        private readonly Catalog _catalog;
        private readonly object _lockable = new object();
        private readonly GrainFactory _grainFactory;
        private readonly ISiloLifecycleSubject _siloLifecycle;
        private readonly IMembershipService _membershipService;
        internal List<GrainService> _grainServices = new List<GrainService>();

        private readonly ILoggerFactory _loggerFactory;
        /// <summary>
        /// Gets the type of this
        /// </summary>
        internal string Name => _siloDetails.Name;
        internal ILocalGrainDirectory LocalGrainDirectory { get { return _localGrainDirectory; } }
        internal IConsistentRingProvider RingProvider { get; private set; }
        internal List<GrainService> GrainServices => _grainServices;

        internal SystemStatus SystemStatus { get; set; }

        internal IServiceProvider Services { get; }

        /// <summary>Gets the address of this silo.</summary>
        public SiloAddress SiloAddress => _siloDetails.SiloAddress;

        /// <summary>
        /// Gets a <see cref="Task"/> which completes once the silo has terminated.
        /// </summary>
        public Task SiloTerminated { get { return _siloTerminatedTask.Task; } } // one event for all types of termination (shutdown, stop and fast kill).

        private bool isFastKilledNeeded; // Set to true if something goes wrong in the shutdown/stop phase

        private LifecycleSchedulingSystemTarget _lifecycleSchedulingSystemTarget;

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
            // Temporarily still require this. Hopefully gone when 2.0 is released.
            _siloDetails = siloDetails;
            SystemStatus = SystemStatus.Creating;

            IOptions<ClusterMembershipOptions> clusterMembershipOptions = services.GetRequiredService<IOptions<ClusterMembershipOptions>>();
            _initTimeout = clusterMembershipOptions.Value.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                _initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), clusterMembershipOptions.Value.MaxJoinAttemptTime);
                _stopTimeout = _initTimeout;
            }

            var localEndpoint = _siloDetails.SiloAddress.Endpoint;

            Services = services;

            //set PropagateActivityId flag from node config
            IOptions<SiloMessagingOptions> messagingOptions = services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            _waitForMessageToBeQueuedForOutbound = messagingOptions.Value.WaitForMessageToBeQueuedForOutboundTime;

            _loggerFactory = Services.GetRequiredService<ILoggerFactory>();
            _logger = _loggerFactory.CreateLogger<Silo>();

            _logger.LogInformation(
                (int)ErrorCode.SiloGcSetting,
                "Silo starting with GC settings: ServerGC={ServerGC} GCLatencyMode={GCLatencyMode}",
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString());
            if (!GCSettings.IsServerGC)
            {
                _logger.LogWarning((int)ErrorCode.SiloGcWarning, "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\">");
                _logger.LogWarning((int)ErrorCode.SiloGcWarning, "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines).");
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var highestLogLevel = _logger.IsEnabled(LogLevel.Trace) ? nameof(LogLevel.Trace) : nameof(LogLevel.Debug);
                _logger.LogWarning(
                    (int)ErrorCode.SiloGcWarning,
                    $"A verbose logging level ({{highestLogLevel}}) is configured. This will impact performance. The recommended log level is {nameof(LogLevel.Information)}.",
                    highestLogLevel);
            }

            _logger.LogInformation(
                (int)ErrorCode.SiloInitializing,
                "-------------- Initializing silo on host {HostName} MachineName {MachineNAme} at {LocalEndpoint}, gen {Generation} --------------",
                _siloDetails.DnsHostName,
                Environment.MachineName,
                localEndpoint,
                _siloDetails.SiloAddress.Generation);
            _logger.LogInformation(
                (int)ErrorCode.SiloInitConfig,
                "Starting silo {SiloName}",
                name);

            try
            {
                _grainFactory = Services.GetRequiredService<GrainFactory>();
            }
            catch (InvalidOperationException exc)
            {
                _logger.LogError(
                    (int)ErrorCode.SiloStartError, exc, "Exception during Silo.Start, GrainFactory was not registered in Dependency Injection container");
                throw;
            }

            _runtimeClient = Services.GetRequiredService<InsideRuntimeClient>();

            // Initialize the message center
            _messageCenter = Services.GetRequiredService<MessageCenter>();
            _messageCenter.SniffIncomingMessage = _runtimeClient.SniffIncomingMessage;

            // Now the router/directory service
            // This has to come after the message center //; note that it then gets injected back into the message center.;
            _localGrainDirectory = Services.GetRequiredService<LocalGrainDirectory>();

            // Now the consistent ring provider
            RingProvider = Services.GetRequiredService<IConsistentRingProvider>();

            _catalog = Services.GetRequiredService<Catalog>();

            _siloStatusOracle = Services.GetRequiredService<ISiloStatusOracle>();
            _membershipService = Services.GetRequiredService<IMembershipService>();

            SystemStatus = SystemStatus.Created;

            _siloLifecycle = Services.GetRequiredService<ISiloLifecycleSubject>();
            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<ISiloLifecycle>> lifecycleParticipants = Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach(ILifecycleParticipant<ISiloLifecycle> participant in lifecycleParticipants)
            {
                participant?.Participate(_siloLifecycle);
            }

            // register all named lifecycle participants
            var namedLifecycleParticipantCollection = Services.GetService<IKeyedServiceCollection<string,ILifecycleParticipant<ISiloLifecycle>>>();
            if (namedLifecycleParticipantCollection?.GetServices(Services)?.Select(s => s.GetService(Services)) is { } namedParticipants)
            {
                foreach (ILifecycleParticipant<ISiloLifecycle> participant in namedParticipants)
                {
                    participant.Participate(_siloLifecycle);
                }
            }

            // add self to lifecycle
            Participate(_siloLifecycle);

            _logger.LogInformation(
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
            _lifecycleSchedulingSystemTarget = Services.GetRequiredService<LifecycleSchedulingSystemTarget>();
            _fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(_lifecycleSchedulingSystemTarget);

            try
            {
                await _lifecycleSchedulingSystemTarget.WorkItemGroup.QueueTask(() => _siloLifecycle.OnStart(cancellationToken), _lifecycleSchedulingSystemTarget);
            }
            catch (Exception exc)
            {
                _logger.LogError((int)ErrorCode.SiloStartError, exc, "Exception during Silo.Start");
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

            if (_membershipService is SystemTarget)
            {
                RegisterSystemTarget((SystemTarget)_membershipService);
            }
        }

        private void InjectDependencies()
        {
            _catalog.SiloStatusOracle = _siloStatusOracle;
            _siloStatusOracle.SubscribeToSiloStatusEvents(_localGrainDirectory);

            // consistentRingProvider is not a system target per say, but it behaves like the localGrainDirectory, so it is here
            _siloStatusOracle.SubscribeToSiloStatusEvents((ISiloStatusListener)RingProvider);

            _siloStatusOracle.SubscribeToSiloStatusEvents(Services.GetRequiredService<DeploymentLoadPublisher>());

            // SystemTarget for provider init calls
            _fallbackScheduler = Services.GetRequiredService<FallbackSystemTarget>();
            RegisterSystemTarget(_fallbackScheduler);
        }

        private Task OnRuntimeInitializeStart(CancellationToken ct)
        {
            lock (_lockable)
            {
                if (!SystemStatus.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(string.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", SystemStatus));

                SystemStatus = SystemStatus.Starting;
            }

            _logger.LogInformation((int)ErrorCode.SiloStarting, "Silo Start()");
            return Task.CompletedTask;
        }

        private void StartTaskWithPerfAnalysis(string taskName, Action task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            task.Invoke();
            stopWatch.Stop();
            _logger.LogInformation(
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
            _logger.LogInformation(
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
                        .WithTimeout(_initTimeout, $"Starting DeploymentLoadPublisher failed due to timeout {_initTimeout}");
                    _logger.LogDebug("Silo deployment load publisher started successfully.");
                }

                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                var healthCheckParticipants = Services.GetService<IEnumerable<IHealthCheckParticipant>>().ToList();
                var membershipOptions = Services.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
                _platformWatchdog = new Watchdog(membershipOptions.LocalHealthDegradationMonitoringPeriod, healthCheckParticipants, _loggerFactory.CreateLogger<Watchdog>());
                _platformWatchdog.Start();
                if (_logger.IsEnabled(LogLevel.Debug)) { _logger.LogDebug("Silo platform watchdog started successfully."); }
            }
            catch (Exception exc)
            {
                _logger.LogError(
                    (int)ErrorCode.Runtime_Error_100330,
                    exc,
                    "Error starting silo {SiloAddress}. Going to FastKill().",
                    SiloAddress);
                throw;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Silo.Start complete: System status = {SystemStatus}", SystemStatus);
            }
        }

        private Task OnBecomeActiveStart(CancellationToken ct)
        {
            SystemStatus = SystemStatus.Running;
            return Task.CompletedTask;
        }

        private async Task OnActiveStart(CancellationToken ct)
        {
            foreach (var grainService in _grainServices)
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
            _grainServices.Add(grainService);

            await grainService.QueueTask(() => grainService.Init(Services)).WithTimeout(_initTimeout, $"GrainService Initializing failed due to timeout {_initTimeout}");
            _logger.LogInformation(
                "Grain Service {GrainServiceType} registered successfully.",
                service.GetType().FullName);
        }

        private async Task StartGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;

            await grainService.QueueTask(grainService.Start).WithTimeout(_initTimeout, $"Starting GrainService failed due to timeout {_initTimeout}");
            _logger.LogInformation("Grain Service {GrainServiceType} started successfully.",service.GetType().FullName);
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
            var cancellationSource = new CancellationTokenSource(_stopTimeout);
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
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug((int)ErrorCode.SiloShuttingDown, "Silo shutdown initiated (graceful)");
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning((int)ErrorCode.SiloShuttingDown, "Silo shutdown initiated (non-graceful)");
                }
            }

            bool stopAlreadyInProgress = false;
            lock (_lockable)
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
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug((int)ErrorCode.SiloStopInProgress, "Silo shutdown in progress. Waiting for shutdown to be completed.");
                }
                var pause = TimeSpan.FromSeconds(1);                

                while (!SystemStatus.Equals(SystemStatus.Terminated))
                {                    
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug((int)ErrorCode.WaitingForSiloStop, "Silo shutdown still in progress...");
                    }
                    await Task.Delay(pause).ConfigureAwait(false);
                }

                await SiloTerminated.ConfigureAwait(false);
                return;
            }

            try
            {
                await _lifecycleSchedulingSystemTarget.QueueTask(() => _siloLifecycle.OnStop(cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                // log final status                
                if (gracefully)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug((int)ErrorCode.SiloShutDown, "Silo shutdown completed (graceful)!");
                    }
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning((int)ErrorCode.SiloShutDown, "Silo shutdown completed (non-graceful)!");
                    }
                }

                // signal to all awaiters that the silo has terminated.
                await Task.Run(() => _siloTerminatedTask.TrySetResult(0)).ConfigureAwait(false);
            }
        }

        private Task OnRuntimeServicesStop(CancellationToken ct)
        {
            if (isFastKilledNeeded || ct.IsCancellationRequested) // No time for this
                return Task.CompletedTask;

            // Start rejecting all silo to silo application messages
            SafeExecute(_messageCenter.BlockApplicationMessages);

            return Task.CompletedTask;
        }

        private async Task OnRuntimeInitializeStop(CancellationToken ct)
        {
            if (_platformWatchdog != null)
            {
                SafeExecute(_platformWatchdog.Stop); // Silo may be dying before platformWatchdog was set up
            }

            try
            {
                await _messageCenter.StopAsync();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error stopping message center");
            }

            SystemStatus = SystemStatus.Terminated;
        }

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            if (isFastKilledNeeded)
                return;

            bool gracefully = !ct.IsCancellationRequested;
            try
            {
                if (gracefully)
                {
                    // Stop LocalGrainDirectory
                    var resolver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _localGrainDirectory.CacheValidator.WorkItemGroup.QueueAction(() =>
                    {
                        try
                        {
                            _localGrainDirectory.Stop();
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
                        await _catalog.DeactivateAllActivations().WithCancellation(ct);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Error deactivating activations");
                    }

                    // Wait for all queued message sent to OutboundMessageQueue before MessageCenter stop and OutboundMessageQueue stop.
                    await Task.WhenAny(Task.Delay(_waitForMessageToBeQueuedForOutbound), ct.WhenCancelled());
                }
            }
            catch (Exception exc)
            {
                _logger.LogError(
                    (int)ErrorCode.SiloFailedToStopMembership,
                    exc,
                    "Failed to shutdown gracefully. About to terminate ungracefully");
                isFastKilledNeeded = true;
            }

            // Stop the gateway
            await _messageCenter.StopAcceptingClientMessages();
        }

        private async Task OnActiveStop(CancellationToken ct)
        {
            if (isFastKilledNeeded || ct.IsCancellationRequested)
                return;

            if (_messageCenter.Gateway != null)
            {
                await _lifecycleSchedulingSystemTarget
                    .QueueTask(() => _messageCenter.Gateway.SendStopSendMessages(_grainFactory))
                    .WithCancellation("Sending gateway disconnection requests failed because the task was cancelled", ct);
            }

            foreach (var grainService in _grainServices)
            {
                await grainService
                    .QueueTask(grainService.Stop)
                    .WithCancellation("Stopping GrainService failed because the task was cancelled", ct);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "{GrainServiceType} Grain Service with Id {GrainServiceId} stopped successfully.",
                        grainService.GetType().FullName,
                        grainService.GetPrimaryKeyLong(out string ignored));
                }
            }
        }

        private void SafeExecute(Action action)
        {
            Utils.SafeExecute(action, _logger, "Silo.Stop");
        }

        internal void RegisterSystemTarget(SystemTarget target) => _catalog.RegisterSystemTarget(target);

        /// <inheritdoc/>
        public override string ToString()
        {
            return _localGrainDirectory.ToString();
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

