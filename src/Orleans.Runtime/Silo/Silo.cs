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
        private readonly MessageCenter messageCenter;
        private readonly ILogger logger;
        private readonly TaskCompletionSource<int> siloTerminatedTask = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InsideRuntimeClient runtimeClient;
        private readonly Watchdog platformWatchdog;
        private readonly TimeSpan waitForMessageToBeQueuedForOutbound;
        private readonly TimeSpan initTimeout;
        private readonly object lockable = new object();
        private readonly GrainFactory grainFactory;
        private readonly ISiloLifecycleSubject siloLifecycle;
        private readonly List<GrainService> grainServices = new List<GrainService>();
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
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // SystemTarget for provider init calls
            this.lifecycleSchedulingSystemTarget = Services.GetRequiredService<LifecycleSchedulingSystemTarget>();

            try
            {
                await this.lifecycleSchedulingSystemTarget.WorkItemGroup.QueueTask(() => this.siloLifecycle.OnStart(cancellationToken), lifecycleSchedulingSystemTarget);
            }
            catch (Exception exc)
            {
                LogErrorSiloStart(logger, exc);
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
                () => CreateGrainServices(), stopWatch);

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
            var activationDirectory = this.Services.GetRequiredService<ActivationDirectory>();
            activationDirectory.RecordNewTarget(grainService);
            grainServices.Add(grainService);

            try
            {
                await grainService.QueueTask(() => grainService.Init(Services)).WaitAsync(this.initTimeout);
            }
            catch (TimeoutException exception)
            {
                LogErrorGrainInitializationTimeout(logger, exception, initTimeout);
                throw;
            }

            LogInfoGrainServiceRegistered(logger, service.GetType().FullName);
        }

        private async Task StartGrainService(IGrainService service)
        {
            var grainService = (GrainService)service;

            try
            {
                await grainService.QueueTask(grainService.Start).WaitAsync(this.initTimeout);
            }
            catch (TimeoutException exception)
            {
                LogErrorGrainStartupTimeout(logger, exception, initTimeout);
                throw;
            }

            LogInfoGrainServiceStarted(logger, service.GetType().FullName);
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
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            bool gracefully = !cancellationToken.IsCancellationRequested;
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
                LogDebugSiloStopInProgress(logger);
                var pause = TimeSpan.FromSeconds(1);

                while (!this.SystemStatus.Equals(SystemStatus.Terminated))
                {
                    LogDebugSiloStopStillInProgress(logger);
                    await Task.Delay(pause).ConfigureAwait(false);
                }

                await this.SiloTerminated.ConfigureAwait(false);
                return;
            }

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
                await this.lifecycleSchedulingSystemTarget.QueueTask(() => this.siloLifecycle.OnStop(cancellationToken)).ConfigureAwait(false);
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

                // signal to all awaiters that the silo has terminated.
                await Task.Run(() => this.siloTerminatedTask.TrySetResult(0)).ConfigureAwait(false);
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
                    await lifecycleSchedulingSystemTarget
                        .QueueTask(() => this.messageCenter.Gateway.SendStopSendMessages(this.grainFactory)).WaitAsync(ct);
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

                LogDebugGrainServiceStopped(logger, grainService.GetType().FullName, grainService.GetGrainId());
            }
        }

        /// <inheritdoc/>
        public override string ToString() => $"Silo: {SiloAddress}";

        private void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeInitialize, (ct) => Task.Run(() => OnRuntimeInitializeStart(ct)), (ct) => Task.Run(() => OnRuntimeInitializeStop(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeServices, (ct) => Task.Run(() => OnRuntimeServicesStart(ct)), (ct) => Task.Run(() => OnRuntimeServicesStop(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.RuntimeGrainServices, (ct) => Task.Run(() => OnRuntimeGrainServicesStart(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.BecomeActive, (ct) => Task.Run(() => OnBecomeActiveStart(ct)), (ct) => Task.Run(() => OnBecomeActiveStop(ct)));
            lifecycle.Subscribe<Silo>(ServiceLifecycleStage.Active, (ct) => Task.Run(() => OnActiveStart(ct)), (ct) => Task.Run(() => OnActiveStop(ct)));
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await StopAsync(cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        public void Dispose()
        {
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                StopAsync(cts.Token).Wait();
            }
            catch
            {
            }
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
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.SiloStopInProgress,
            Message = "Silo shutdown still in progress."
        )]
        private static partial void LogDebugSiloStopStillInProgress(ILogger logger);

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

    // A dummy system target for fallback scheduler
    internal sealed class LifecycleSchedulingSystemTarget : SystemTarget
    {
        public LifecycleSchedulingSystemTarget(SystemTargetShared shared)
            : base(Constants.LifecycleSchedulingSystemTargetType, shared)
        {
            shared.ActivationDirectory.RecordNewTarget(this);
        }
    }
}
