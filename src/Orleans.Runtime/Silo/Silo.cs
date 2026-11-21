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
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;
using Orleans.Services;
using Orleans.Configuration;
using Orleans.Internal;
using System.Net;

namespace Orleans.Runtime
{
    /// <summary>
    /// Orleans silo.
    /// </summary>
    public sealed partial class Silo : IAsyncDisposable, IDisposable
    {
        /// <summary>Standard name for Primary silo. </summary>
        public const string PrimarySiloName = "Primary";
        private readonly ILocalSiloDetails siloDetails;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
            Justification = "The host service container owns this service and disposes it after silo shutdown.")]
        private readonly MessageCenter messageCenter;
        private readonly ILogger logger;
        private readonly TaskCompletionSource<int> siloTerminatedTask = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InsideRuntimeClient runtimeClient;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
            Justification = "The host service container owns this service and disposes it after silo shutdown.")]
        private readonly Watchdog platformWatchdog;
        private readonly TimeSpan waitForMessageToBeQueuedForOutbound;
        private readonly TimeSpan initTimeout;
        private readonly Lock lockable = new();
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
            Justification = "Has no timer or linked tokens to release, and a start may still observe it while the silo is disposed.")]
        private readonly CancellationTokenSource stopRequested = new();
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
            Justification = "Has no timer or linked tokens to release, and a stop may still observe it while the silo is disposed.")]
        private readonly CancellationTokenSource stopEscalation = new();
        private Task? startTask;
        private readonly GrainFactory grainFactory;
        private readonly ISiloLifecycleSubject siloLifecycle;
        private readonly List<GrainService> grainServices = new List<GrainService>();
        private Task? grainServiceInit;
        private bool grainServicesStarting;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
            Justification = "The host service container owns this service and disposes it after silo shutdown.")]
        private readonly ILoggerFactory loggerFactory;

        internal IConsistentRingProvider RingProvider { get; }

        internal SystemStatus SystemStatus { get; set; }

        internal IServiceProvider Services { get; }

        /// <summary>Gets the address of this silo.</summary>
        public SiloAddress SiloAddress => this.siloDetails.SiloAddress;

        /// <summary>
        /// Gets a <see cref="Task"/> which completes once the silo has terminated.
        /// </summary>
        public Task SiloTerminated { get { return this.siloTerminatedTask.Task; } } // one event for all types of termination (shutdown, stop and fast kill).

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
            SystemStatus = SystemStatus.Creating;
            Services = services;
            RingProvider = services.GetRequiredService<IConsistentRingProvider>();
            platformWatchdog = services.GetRequiredService<Watchdog>();
            this.siloDetails = siloDetails;

            IOptions<ClusterMembershipOptions> clusterMembershipOptions = services.GetRequiredService<IOptions<ClusterMembershipOptions>>();
            initTimeout = clusterMembershipOptions.Value.MaxJoinAttemptTime;
            if (Debugger.IsAttached)
            {
                initTimeout = StandardExtensions.Max(TimeSpan.FromMinutes(10), clusterMembershipOptions.Value.MaxJoinAttemptTime);
            }

            var localEndpoint = this.siloDetails.SiloAddress.Endpoint;

            //set PropagateActivityId flag from node config
            IOptions<SiloMessagingOptions> messagingOptions = services.GetRequiredService<IOptions<SiloMessagingOptions>>();
            this.waitForMessageToBeQueuedForOutbound = messagingOptions.Value.WaitForMessageToBeQueuedForOutboundTime;

            this.loggerFactory = this.Services.GetRequiredService<ILoggerFactory>();
            logger = this.loggerFactory.CreateLogger<Silo>();

            LogSiloStartingWithGC(logger, GCSettings.IsServerGC, GCSettings.LatencyMode);
            if (!GCSettings.IsServerGC)
            {
                LogWarningSiloGcNotRunningWithServerGC(logger);
                LogWarningSiloGcMultiCoreSystem(logger);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var highestLogLevel = logger.IsEnabled(LogLevel.Trace) ? nameof(LogLevel.Trace) : nameof(LogLevel.Debug);
                LogWarningSiloGcVerboseLOggingConfigured(logger, highestLogLevel);
            }

            LogInfoSiloInitializing(logger, siloDetails.DnsHostName, Environment.MachineName, localEndpoint, siloDetails.SiloAddress.Generation);
            LogInfoSiloInitConfig(logger, siloDetails.Name);

            try
            {
                grainFactory = Services.GetRequiredService<GrainFactory>();
            }
            catch (InvalidOperationException exc)
            {
                LogErrorSiloStartGrainFactoryNotRegistered(logger, exc);
                throw;
            }

            runtimeClient = Services.GetRequiredService<InsideRuntimeClient>();

            // Initialize the message center
            messageCenter = Services.GetRequiredService<MessageCenter>();
            messageCenter.SniffIncomingMessage = runtimeClient.SniffIncomingMessage;

            this.SystemStatus = SystemStatus.Created;

            this.siloLifecycle = this.Services.GetRequiredService<ISiloLifecycleSubject>();
            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<ISiloLifecycle>> lifecycleParticipants = this.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach (ILifecycleParticipant<ISiloLifecycle> participant in lifecycleParticipants)
            {
                participant?.Participate(this.siloLifecycle);
            }

            // add self to lifecycle
            this.Participate(this.siloLifecycle);

            LogInfoSiloInitializingFinished(logger, SiloAddress, new(SiloAddress));
        }

        /// <summary>
        /// Starts the silo.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token which can be used to cancel the operation.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        /// <remarks>
        /// If the start fails or is cancelled, the lifecycle stages which had already started are stopped before this
        /// method rethrows, and <see cref="SiloTerminated"/> completes. A silo can only be started once.
        /// </remarks>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (lockable)
            {
                if (startTask is not null || !this.SystemStatus.Equals(SystemStatus.Created))
                {
                    throw new InvalidOperationException($"Calling Silo.Start() on a silo which is not in the {nameof(SystemStatus.Created)} state or has already been started. This silo is in the {this.SystemStatus} state.");
                }

                // Run on the thread pool so that the start never runs, or rolls back, while this lock is held.
                // A start whose token is already cancelled does not run at all, as before.
                return startTask = Task.Run(() => StartCoreAsync(cancellationToken), cancellationToken);
            }
        }

        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            // StopAsync cancels the start through stopRequested, so that a silo which is still starting can be stopped.
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopRequested.Token);
            try
            {
                await this.siloLifecycle.OnStart(startCancellation.Token);
            }
            catch (Exception exc)
            {
                LogErrorSiloStart(logger, exc);

                // Stop the stages which had already started (for example, the silo listener and the membership row),
                // unless StopAsync was called during the start: it waits for the start to end and then stops the silo.
                // The stop is graceful, but a later StopAsync or DisposeAsync whose token is cancelled hurries it through stopEscalation.
                if (TryBeginStop(gracefully: true))
                {
                    await StopCoreAsync(gracefully: true, stopEscalation.Token);
                }

                throw;
            }
        }

        private Task OnRuntimeInitializeStart(CancellationToken ct)
        {
            lock (lockable)
            {
                if (!this.SystemStatus.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(string.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", this.SystemStatus));

                this.SystemStatus = SystemStatus.Starting;
            }

            LogInfoSiloStarting(logger);
            return Task.CompletedTask;
        }

        private void StartTaskWithPerfAnalysis(string taskName, Action task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            task.Invoke();
            stopWatch.Stop();

            LogInfoSiloStartPerfMeasure(logger, taskName, stopWatch.ElapsedMilliseconds);
        }

        private async Task StartAsyncTaskWithPerfAnalysis(string taskName, Func<Task> task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            await task.Invoke();
            stopWatch.Stop();

            LogInfoSiloStartPerfMeasure(logger, taskName, stopWatch.ElapsedMilliseconds);
        }

        private Task OnRuntimeServicesStart(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private async Task OnRuntimeGrainServicesStart(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            // Load and init grain services before silo becomes active.
            await StartAsyncTaskWithPerfAnalysis("Init grain services",
                () => CreateGrainServices(ct), stopWatch);

            try
            {
                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                this.platformWatchdog.Start();
            }
            catch (Exception exc)
            {
                LogErrorStartingSiloGoingToFastKill(logger, exc, SiloAddress);
                throw;
            }

            LogDebugSiloStartComplete(logger, this.SystemStatus);
        }

        private Task OnBecomeActiveStart(CancellationToken ct)
        {
            lock (lockable)
            {
                // A stop requested during the start has already moved the silo past Starting; keep that status.
                if (this.SystemStatus.Equals(SystemStatus.Starting))
                {
                    this.SystemStatus = SystemStatus.Running;
                }
            }

            return Task.CompletedTask;
        }

        private async Task OnActiveStart(CancellationToken ct)
        {
            grainServicesStarting = true;
            foreach (var grainService in grainServices)
            {
                await StartGrainService(grainService, ct);
            }
        }

        private async Task CreateGrainServices(CancellationToken cancellationToken)
        {
            var grainServices = this.Services.GetServices<IGrainService>();
            foreach (var grainService in grainServices)
            {
                await RegisterGrainService(grainService, cancellationToken);
            }
        }

        private async Task RegisterGrainService(IGrainService service, CancellationToken cancellationToken)
        {
            var grainService = (GrainService)service;
            var activationDirectory = this.Services.GetRequiredService<ActivationDirectory>();
            activationDirectory.RecordNewTarget(grainService);
            grainServices.Add(grainService);

            // Kept so that a stop can wait for an Init whose wait timed out or was cancelled; Inits run one at a time.
            var init = grainServiceInit = grainService.QueueTask(() => grainService.Init(Services));
            try
            {
                await init.WaitAsync(this.initTimeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                LogErrorGrainInitializationTimeout(logger, exception, initTimeout);
                throw;
            }

            LogInfoGrainServiceRegistered(logger, service.GetType().FullName!);
        }

        private async Task StartGrainService(IGrainService service, CancellationToken cancellationToken)
        {
            var grainService = (GrainService)service;

            try
            {
                await grainService.QueueTask(grainService.Start).WaitAsync(this.initTimeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                LogErrorGrainStartupTimeout(logger, exception, initTimeout);
                throw;
            }

            LogInfoGrainServiceStarted(logger, service.GetType().FullName!);
        }

        /// <summary>
        /// Gracefully stop the run time system only, but not the application.
        /// Applications requests would be abruptly terminated, while the internal system state gracefully stopped and saved as much as possible.
        /// Grains are not deactivated.
        /// </summary>
        public void Stop()
        {
        }

        /// <summary>
        /// Gracefully stop the run time system only, but not the application.
        /// Applications requests would be abruptly terminated, while the internal system state gracefully stopped and saved as much as possible.
        /// </summary>
        /// <param name="cancellationToken">
        /// A cancellation token which can be used to promptly terminate the silo.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        /// <remarks>
        /// A silo which is still starting is stopped too: the start is cancelled, and once it has ended, the stages
        /// which had started are stopped. If a stop is already in progress, this method waits for it to complete,
        /// or until <paramref name="cancellationToken"/> is cancelled.
        /// </remarks>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            bool gracefully = !cancellationToken.IsCancellationRequested;
            if (!TryBeginStop(gracefully))
            {
                LogDebugSiloStopInProgress(logger);

                // A caller whose token is cancelled hurries the stop in progress, for example the rollback of a failed start.
                using var escalation = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), stopEscalation);
                await this.SiloTerminated.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            Task? pendingStart;
            lock (lockable)
            {
                pendingStart = startTask;
            }

            try
            {
                if (pendingStart is { IsCompleted: false })
                {
                    // Stopping the stages while they are still starting would race with their start, so cancel the start
                    // and wait for it to end first. It does not roll back by itself, since this stop has begun.
                    // The silo's own start waits observe the cancellation; other lifecycle participants are expected to.
                    stopRequested.Cancel();
                }
            }
            finally
            {
                // This caller owns the stop, so it must run even if a cancellation callback registered by a start
                // participant throws; the callback's exception propagates once the silo has terminated.
                if (pendingStart is not null)
                {
                    await pendingStart.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopEscalation.Token);
                await StopCoreAsync(gracefully, stopCancellation.Token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Moves the silo to <see cref="SystemStatus.ShuttingDown"/> or <see cref="SystemStatus.Stopping"/>, unless a stop has already begun.
        /// </summary>
        /// <returns><see langword="true"/> if the caller owns the stop and must run <see cref="StopCoreAsync"/>.</returns>
        private bool TryBeginStop(bool gracefully)
        {
            lock (lockable)
            {
                if (this.SystemStatus.Equals(SystemStatus.Stopping) ||
                    this.SystemStatus.Equals(SystemStatus.ShuttingDown) ||
                    this.SystemStatus.Equals(SystemStatus.Terminated))
                {
                    return false;
                }

                this.SystemStatus = gracefully ? SystemStatus.ShuttingDown : SystemStatus.Stopping;
                return true;
            }
        }

        private async Task StopCoreAsync(bool gracefully, CancellationToken cancellationToken)
        {
            if (gracefully)
            {
                LogSiloShuttingDown(logger, LogLevel.Debug, "graceful");
            }
            else
            {
                LogSiloShuttingDown(logger, LogLevel.Debug, "non-graceful");
            }

            try
            {
                await Task.Run(() => this.siloLifecycle.OnStop(cancellationToken), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // log final status
                if (gracefully)
                {
                    LogSiloShutDown(logger, LogLevel.Debug, "graceful");
                }
                else
                {
                    LogSiloShutDown(logger, LogLevel.Warning, "non-graceful");
                }

                // The RuntimeInitialize stage sets Terminated when it stops; a silo stopped before that stage started does not reach it.
                this.SystemStatus = SystemStatus.Terminated;

                // signal to all awaiters that the silo has terminated.
                await Task.Run(() => this.siloTerminatedTask.TrySetResult(0)).ConfigureAwait(false);
            }
        }

        private async Task OnRuntimeGrainServicesStop(CancellationToken ct)
        {
            // The Active stage stops the grain services once it has started. A start which ended before that stage
            // leaves them to this stage, and an Init whose wait was cancelled or timed out may still be running: stop the
            // services, so that the Init can observe the stop through the service's stopped token, and wait for it.
            // Like the Active stage, a non-graceful stop skips this.
            if (grainServicesStarting || ct.IsCancellationRequested)
                return;

            foreach (var grainService in grainServices)
            {
                try
                {
                    await grainService.QueueTask(grainService.Stop).WaitAsync(ct);
                }
                catch (Exception exception)
                {
                    LogErrorStoppingGrainService(logger, grainService, exception);
                    continue;
                }

                LogDebugGrainServiceStopped(logger, grainService.GetType().FullName!, grainService.GetGrainId());
            }

            if (grainServiceInit is { } init)
            {
                await init.WaitAsync(ct).SuppressThrowing();
            }
        }

        private Task OnRuntimeServicesStop(CancellationToken ct)
        {
            // Start rejecting all silo to silo application messages
            messageCenter.BlockApplicationMessages();

            return Task.CompletedTask;
        }

        private async Task OnRuntimeInitializeStop(CancellationToken ct)
        {
            try
            {
                await messageCenter.StopAsync();
            }
            catch (Exception exception)
            {
                LogErrorStoppingMessageCenter(logger, exception);
            }

            SystemStatus = SystemStatus.Terminated;
        }

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            try
            {
                try
                {
                    var catalog = this.Services.GetRequiredService<Catalog>();
                    await catalog.DeactivateAllActivations(ct);
                }
                catch (Exception exception)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        LogErrorDeactivatingActivations(logger, exception);
                    }
                    else
                    {
                        LogWarningSomeGrainsFailedToDeactivate(logger);
                    }
                }

                // Wait for all queued message sent to OutboundMessageQueue before MessageCenter stop and OutboundMessageQueue stop.
                await Task.Delay(waitForMessageToBeQueuedForOutbound, ct).SuppressThrowing();
            }
            catch (Exception exc)
            {
                LogErrorSiloFailedToStopMembership(logger, exc);
            }

            // Stop the gateway
            await messageCenter.StopAcceptingClientMessages();
        }

        private async Task OnActiveStop(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return;

            if (this.messageCenter.Gateway != null)
            {
                try
                {
                    await Task.Run(() => this.messageCenter.Gateway.SendStopSendMessages(this.grainFactory, ct), CancellationToken.None).WaitAsync(ct);
                }
                catch (Exception exception)
                {
                    LogErrorSendingDisconnectRequests(logger, exception);
                    if (!ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            foreach (var grainService in grainServices)
            {
                try
                {
                    await grainService
                        .QueueTask(grainService.Stop)
                        .WaitAsync(ct);
                }
                catch (Exception exception)
                {
                    LogErrorStoppingGrainService(logger, grainService, exception);
                    if (!ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }

                LogDebugGrainServiceStopped(logger, grainService.GetType().FullName!, grainService.GetGrainId());
            }
        }

        /// <inheritdoc/>
        public override string ToString() => $"Silo: {SiloAddress}";

        private void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeInitialize, RunOnRuntimeInitializeStart, RunOnRuntimeInitializeStop);
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeServices, RunOnRuntimeServicesStart, RunOnRuntimeServicesStop);
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeGrainServices, RunOnRuntimeGrainServicesStart, RunOnRuntimeGrainServicesStop);
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.BecomeActive, RunOnBecomeActiveStart);
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.GrainDeactivation, NoOpStart, RunOnBecomeActiveStop);
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.Active, RunOnActiveStart, RunOnActiveStop);

            // Stage callbacks are dispatched onto the thread pool so that any blocking
            // work in the silo start/stop paths does not stall the lifecycle scheduler.
            static Task NoOpStart(CancellationToken ct) => Task.CompletedTask;
            Task RunOnRuntimeInitializeStart(CancellationToken ct) => Task.Run(() => OnRuntimeInitializeStart(ct), CancellationToken.None);
            Task RunOnRuntimeInitializeStop(CancellationToken ct) => Task.Run(() => OnRuntimeInitializeStop(ct), CancellationToken.None);
            Task RunOnRuntimeServicesStart(CancellationToken ct) => Task.Run(() => OnRuntimeServicesStart(ct), CancellationToken.None);
            Task RunOnRuntimeServicesStop(CancellationToken ct) => Task.Run(() => OnRuntimeServicesStop(ct), CancellationToken.None);
            Task RunOnRuntimeGrainServicesStart(CancellationToken ct) => Task.Run(() => OnRuntimeGrainServicesStart(ct), CancellationToken.None);
            Task RunOnRuntimeGrainServicesStop(CancellationToken ct) => Task.Run(() => OnRuntimeGrainServicesStop(ct), CancellationToken.None);
            Task RunOnBecomeActiveStart(CancellationToken ct) => Task.Run(() => OnBecomeActiveStart(ct), CancellationToken.None);
            Task RunOnBecomeActiveStop(CancellationToken ct) => Task.Run(() => OnBecomeActiveStop(ct), CancellationToken.None);
            Task RunOnActiveStart(CancellationToken ct) => Task.Run(() => OnActiveStart(ct), CancellationToken.None);
            Task RunOnActiveStop(CancellationToken ct) => Task.Run(() => OnActiveStop(ct), CancellationToken.None);
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync() => new(StopForDisposalAsync());

        /// <inheritdoc/>
        public void Dispose()
        {
#pragma warning disable RS0030 // IDisposable requires synchronously completing silo shutdown.
            StopForDisposalAsync().Wait();
#pragma warning restore RS0030
        }

        /// <summary>
        /// Stops the silo non-gracefully and completes once it has terminated, including when another stop, or the
        /// rollback of a failed start, owns the shutdown.
        /// </summary>
        private async Task StopForDisposalAsync()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The cancelled token makes the stop non-graceful and hurries a stop which is already in progress. When
            // another caller owns that stop, StopAsync stops waiting for it as soon as the token is cancelled, so wait
            // for termination here: the services must not be released while the stop is still using them.
            await StopAsync(cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await this.SiloTerminated.ConfigureAwait(false);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Silo starting with GC settings: ServerGC={ServerGC} GCLatencyMode={GCLatencyMode}"
        )]
        private static partial void LogSiloStartingWithGC(ILogger logger, bool serverGC, GCLatencyMode gcLatencyMode);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.SiloGcWarning,
            Message = "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\">"
        )]
        private static partial void LogWarningSiloGcNotRunningWithServerGC(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.SiloGcWarning,
            Message = "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines)."
        )]
        private static partial void LogWarningSiloGcMultiCoreSystem(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.SiloGcWarning,
            Message = $"A verbose logging level ({{HighestLogLevel}}) is configured. This will impact performance. The recommended log level is {nameof(LogLevel.Information)}."
        )]
        private static partial void LogWarningSiloGcVerboseLOggingConfigured(ILogger logger, string highestLogLevel);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.SiloInitializing,
            Message = "-------------- Initializing silo on host {HostName} MachineName {MachineName} at {LocalEndpoint}, gen {Generation} --------------"
        )]
        private static partial void LogInfoSiloInitializing(ILogger logger, string hostName, string machineName, IPEndPoint localEndpoint, int generation);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.SiloInitConfig,
            Message = "Starting silo {SiloName}"
        )]
        private static partial void LogInfoSiloInitConfig(ILogger logger, string siloName);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.SiloStartError,
            Message = "Exception during Silo.Start, GrainFactory was not registered in Dependency Injection container"
        )]
        private static partial void LogErrorSiloStartGrainFactoryNotRegistered(ILogger logger, Exception exc);

        private readonly struct SiloAddressConsistentHashCodeLogValue(SiloAddress siloAddress)
        {
            public override string ToString() => siloAddress.GetConsistentHashCode().ToString("X");
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.SiloInitializingFinished,
            Message = "-------------- Started silo {SiloAddress}, ConsistentHashCode {HashCode} --------------"
        )]
        private static partial void LogInfoSiloInitializingFinished(ILogger logger, SiloAddress siloAddress, SiloAddressConsistentHashCodeLogValue hashCode);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.SiloStartError,
            Message = "Exception during Silo.Start"
        )]
        private static partial void LogErrorSiloStart(ILogger logger, Exception exc);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.SiloStarting,
            Message = "Silo Start()"
        )]
        private static partial void LogInfoSiloStarting(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.SiloStartPerfMeasure,
            Message = "{TaskName} took {ElapsedMilliseconds} milliseconds to finish"
        )]
        private static partial void LogInfoSiloStartPerfMeasure(ILogger logger, string taskName, long elapsedMilliseconds);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Runtime_Error_100330,
            Message = "Error starting silo {SiloAddress}. Going to FastKill()."
        )]
        private static partial void LogErrorStartingSiloGoingToFastKill(ILogger logger, Exception exc, SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo.Start complete: System status = {SystemStatus}"
        )]
        private static partial void LogDebugSiloStartComplete(ILogger logger, SystemStatus systemStatus);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "GrainService initialization timed out after '{Timeout}'."
        )]
        private static partial void LogErrorGrainInitializationTimeout(ILogger logger, Exception exception, TimeSpan timeout);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain Service {GrainServiceType} registered successfully."
        )]
        private static partial void LogInfoGrainServiceRegistered(ILogger logger, string grainServiceType);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "GrainService startup timed out after '{Timeout}'."
        )]
        private static partial void LogErrorGrainStartupTimeout(ILogger logger, Exception exception, TimeSpan timeout);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Grain Service {GrainServiceType} started successfully."
        )]
        private static partial void LogInfoGrainServiceStarted(ILogger logger, string grainServiceType);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.SiloStopInProgress,
            Message = "Silo shutdown in progress. Waiting for shutdown to be completed."
        )]
        private static partial void LogDebugSiloStopInProgress(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.SiloShuttingDown,
            Message = "Silo shutdown initiated ({Gracefully})."
        )]
        private static partial void LogSiloShuttingDown(ILogger logger, LogLevel logLevel, string gracefully);

        [LoggerMessage(
            EventId = (int)ErrorCode.SiloShutDown,
            Message = "Silo shutdown completed ({Gracefully})."
        )]
        private static partial void LogSiloShutDown(ILogger logger, LogLevel logLevel, string gracefully);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error stopping message center."
        )]
        private static partial void LogErrorStoppingMessageCenter(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error deactivating activations."
        )]
        private static partial void LogErrorDeactivatingActivations(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Some grains failed to deactivate promptly."
        )]
        private static partial void LogWarningSomeGrainsFailedToDeactivate(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to stop gracefully. About to terminate ungracefully."
        )]
        private static partial void LogErrorSiloFailedToStopMembership(ILogger logger, Exception exc);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error sending disconnect requests to connected clients."
        )]
        private static partial void LogErrorSendingDisconnectRequests(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Stopping GrainService '{GrainService}' failed."
        )]
        private static partial void LogErrorStoppingGrainService(ILogger logger, GrainService grainService, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{GrainServiceType} Grain Service with Id {GrainServiceId} stopped successfully."
        )]
        private static partial void LogDebugGrainServiceStopped(ILogger logger, string grainServiceType, GrainId grainServiceId);
    }
}
