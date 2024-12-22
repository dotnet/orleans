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
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Orleans silo.
    /// </summary>
    public sealed class Silo : IAsyncDisposable, IDisposable
    {
        /// <summary>Standard name for Primary silo. </summary>
        public const string PrimarySiloName = "Primary";
        private readonly ILocalSiloDetails siloDetails;
        private readonly MessageCenter messageCenter;
        private readonly ILogger logger;
        private readonly TaskCompletionSource<int> siloTerminatedTask = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InsideRuntimeClient runtimeClient;
        private readonly SystemTarget fallbackScheduler;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly Watchdog platformWatchdog;
        private readonly TimeSpan waitForMessageToBeQueuedForOutbound;
        private readonly TimeSpan initTimeout;
        private readonly Catalog catalog;
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
            fallbackScheduler = services.GetRequiredService<FallbackSystemTarget>();
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

            SiloLoggerMessages.SiloGcSetting(logger, GCSettings.IsServerGC, GCSettings.LatencyMode.ToString());
            if (!GCSettings.IsServerGC)
            {
                SiloLoggerMessages.SiloGcWarning(logger, "Note: Silo not running with ServerGC turned on - recommend checking app config : <configuration>-<runtime>-<gcServer enabled=\"true\">");
                SiloLoggerMessages.SiloGcWarning(logger, "Note: ServerGC only kicks in on multi-core systems (settings enabling ServerGC have no effect on single-core machines).");
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var highestLogLevel = logger.IsEnabled(LogLevel.Trace) ? nameof(LogLevel.Trace) : nameof(LogLevel.Debug);
                SiloLoggerMessages.SiloGcWarning(logger, $"A verbose logging level ({{highestLogLevel}}) is configured. This will impact performance. The recommended log level is {nameof(LogLevel.Information)}.", highestLogLevel);
            }

            SiloLoggerMessages.SiloInitializing(logger, this.siloDetails.DnsHostName, Environment.MachineName, localEndpoint, this.siloDetails.SiloAddress.Generation);
            SiloLoggerMessages.SiloInitConfig(logger, siloDetails.Name);

            try
            {
                grainFactory = Services.GetRequiredService<GrainFactory>();
            }
            catch (InvalidOperationException exc)
            {
                SiloLoggerMessages.SiloStartError(logger, exc, "Exception during Silo.Start, GrainFactory was not registered in Dependency Injection container");
                throw;
            }

            runtimeClient = Services.GetRequiredService<InsideRuntimeClient>();

            // Initialize the message center
            messageCenter = Services.GetRequiredService<MessageCenter>();
            messageCenter.SniffIncomingMessage = runtimeClient.SniffIncomingMessage;

            catalog = Services.GetRequiredService<Catalog>();

            siloStatusOracle = Services.GetRequiredService<ISiloStatusOracle>();

            this.SystemStatus = SystemStatus.Created;

            this.siloLifecycle = this.Services.GetRequiredService<ISiloLifecycleSubject>();
            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<ISiloLifecycle>> lifecycleParticipants = this.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
            foreach(ILifecycleParticipant<ISiloLifecycle> participant in lifecycleParticipants)
            {
                participant?.Participate(this.siloLifecycle);
            }

            // add self to lifecycle
            this.Participate(this.siloLifecycle);

            SiloLoggerMessages.SiloInitializingFinished(logger, SiloAddress.ToString(), SiloAddress.GetConsistentHashCode().ToString("X"));
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
            catalog.RegisterSystemTarget(lifecycleSchedulingSystemTarget);

            try
            {
                await this.lifecycleSchedulingSystemTarget.WorkItemGroup.QueueTask(() => this.siloLifecycle.OnStart(cancellationToken), lifecycleSchedulingSystemTarget);
            }
            catch (Exception exc)
            {
                SiloLoggerMessages.SiloStartError(logger, exc, "Exception during Silo.Start");
                throw;
            }
        }

        private void CreateSystemTargets()
        {
            var siloControl = ActivatorUtilities.CreateInstance<SiloControl>(Services);
            catalog.RegisterSystemTarget(siloControl);

        }

        private void InjectDependencies()
        {
            catalog.SiloStatusOracle = this.siloStatusOracle;

            // SystemTarget for provider init calls
            catalog.RegisterSystemTarget(fallbackScheduler);
        }

        private Task OnRuntimeInitializeStart(CancellationToken ct)
        {
            lock (lockable)
            {
                if (!this.SystemStatus.Equals(SystemStatus.Created))
                    throw new InvalidOperationException(string.Format("Calling Silo.Start() on a silo which is not in the Created state. This silo is in the {0} state.", this.SystemStatus));

                this.SystemStatus = SystemStatus.Starting;
            }

            SiloLoggerMessages.SiloStarting(logger);
            return Task.CompletedTask;
        }

        private void StartTaskWithPerfAnalysis(string taskName, Action task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            task.Invoke();
            stopWatch.Stop();
            SiloLoggerMessages.SiloStartPerfMeasure(logger, taskName, stopWatch.ElapsedMilliseconds);
        }

        private async Task StartAsyncTaskWithPerfAnalysis(string taskName, Func<Task> task, Stopwatch stopWatch)
        {
            stopWatch.Restart();
            await task.Invoke();
            stopWatch.Stop();
            SiloLoggerMessages.SiloStartPerfMeasure(logger, taskName, stopWatch.ElapsedMilliseconds);
        }

        private Task OnRuntimeServicesStart(CancellationToken ct)
        {
            //TODO: Setup all (or as many as possible) of the class started in this call to work directly with lifecycle
            var stopWatch = Stopwatch.StartNew();

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
                // Start background timer tick to watch for platform execution stalls, such as when GC kicks in
                this.platformWatchdog.Start();
            }
            catch (Exception exc)
            {
                SiloLoggerMessages.Runtime_Error_100330(logger, exc, "Error starting silo {SiloAddress}. Going to FastKill().", this.SiloAddress);
                throw;
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                SiloLoggerMessages.SiloStartComplete(logger, this.SystemStatus);
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
            catalog.RegisterSystemTarget(grainService);
            grainServices.Add(grainService);

            try
            {
                await grainService.QueueTask(() => grainService.Init(Services)).WaitAsync(this.initTimeout);
            }
            catch (TimeoutException exception)
            {
                SiloLoggerMessages.GrainServiceInitializationTimeout(logger, exception, initTimeout);
                throw;
            }

            SiloLoggerMessages.GrainServiceRegistered(logger, service.GetType().FullName);
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
                SiloLoggerMessages.GrainServiceStartupTimeout(logger, exception, initTimeout);
                throw;
            }

            SiloLoggerMessages.GrainServiceStarted(logger, service.GetType().FullName);
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
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    SiloLoggerMessages.SiloStopInProgress(logger);
                }
                var pause = TimeSpan.FromSeconds(1);

                while (!this.SystemStatus.Equals(SystemStatus.Terminated))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        SiloLoggerMessages.WaitingForSiloStop(logger);
                    }
                    await Task.Delay(pause).ConfigureAwait(false);
                }

                await this.SiloTerminated.ConfigureAwait(false);
                return;
            }

            if (gracefully)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    SiloLoggerMessages.SiloShuttingDown(logger, "Silo shutdown initiated (graceful)");
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    SiloLoggerMessages.SiloShuttingDown(logger, "Silo shutdown initiated (non-graceful)");
                }
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
                        SiloLoggerMessages.SiloShutDown(logger, "Silo shutdown completed (graceful)!");
                    }
                }
                else
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        SiloLoggerMessages.SiloShutDown(logger, "Silo shutdown completed (non-graceful)!");
                    }
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
                SiloLoggerMessages.ErrorStoppingMessageCenter(logger, exception);
            }

            SystemStatus = SystemStatus.Terminated;
        }

        private async Task OnBecomeActiveStop(CancellationToken ct)
        {
            try
            {
                try
                {
                    await catalog.DeactivateAllActivations(ct);
                }
                catch (Exception exception)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        SiloLoggerMessages.ErrorDeactivatingActivations(logger, exception);
                    }
                    else
                    {
                        SiloLoggerMessages.Warning(logger, "Some grains failed to deactivate promptly.");
                    }
                }

                // Wait for all queued message sent to OutboundMessageQueue before MessageCenter stop and OutboundMessageQueue stop.
                await Task.WhenAny(Task.Delay(waitForMessageToBeQueuedForOutbound), ct.WhenCancelled());
            }
            catch (Exception exc)
            {
                SiloLoggerMessages.SiloFailedToStopMembership(logger, exc);
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
                    SiloLoggerMessages.ErrorSendingGatewayDisconnectionRequests(logger, exception);
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
                    SiloLoggerMessages.ErrorStoppingGrainService(logger, exception, grainService);
                    if (!ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }

                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    SiloLoggerMessages.GrainServiceStopped(logger, grainService.GetType().FullName, grainService.GetGrainId().ToString());
                }
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

    internal static partial class SiloLoggerMessages
    {
        [LoggerMessage(1, LogLevel.Information, "Silo starting with GC settings: ServerGC={ServerGC} GCLatencyMode={GCLatencyMode}")]
        public static partial void SiloGcSetting(ILogger logger, bool ServerGC, string GCLatencyMode);

        [LoggerMessage(2, LogLevel.Warning, "{Message}")]
        public static partial void SiloGcWarning(ILogger logger, string Message);

        [LoggerMessage(3, LogLevel.Information, "-------------- Initializing silo on host {HostName} MachineName {MachineName} at {LocalEndpoint}, gen {Generation} --------------")]
        public static partial void SiloInitializing(ILogger logger, string HostName, string MachineName, System.Net.IPEndPoint LocalEndpoint, int Generation);

        [LoggerMessage(4, LogLevel.Information, "Starting silo {SiloName}")]
        public static partial void SiloInitConfig(ILogger logger, string SiloName);

        [LoggerMessage(5, LogLevel.Error, "{Message}")]
        public static partial void SiloStartError(ILogger logger, Exception exception, string Message);

        [LoggerMessage(6, LogLevel.Information, "-------------- Started silo {SiloAddress}, ConsistentHashCode {HashCode} --------------")]
        public static partial void SiloInitializingFinished(ILogger logger, string SiloAddress, string HashCode);

        [LoggerMessage(7, LogLevel.Information, "Silo Start()")]
        public static partial void SiloStarting(ILogger logger);

        [LoggerMessage(8, LogLevel.Information, "{TaskName} took {ElapsedMilliseconds} milliseconds to finish")]
        public static partial void SiloStartPerfMeasure(ILogger logger, string TaskName, long ElapsedMilliseconds);

        [LoggerMessage(9, LogLevel.Error, "Error starting silo {SiloAddress}. Going to FastKill().")]
        public static partial void Runtime_Error_100330(ILogger logger, Exception exception, string Message, SiloAddress SiloAddress);

        [LoggerMessage(10, LogLevel.Debug, "Silo.Start complete: System status = {SystemStatus}")]
        public static partial void SiloStartComplete(ILogger logger, SystemStatus SystemStatus);

        [LoggerMessage(11, LogLevel.Error, "GrainService initialization timed out after '{Timeout}'.")]
        public static partial void GrainServiceInitializationTimeout(ILogger logger, Exception exception, TimeSpan Timeout);

        [LoggerMessage(12, LogLevel.Information, "Grain Service {GrainServiceType} registered successfully.")]
        public static partial void GrainServiceRegistered(ILogger logger, string GrainServiceType);

        [LoggerMessage(13, LogLevel.Error, "GrainService startup timed out after '{Timeout}'.")]
        public static partial void GrainServiceStartupTimeout(ILogger logger, Exception exception, TimeSpan Timeout);

        [LoggerMessage(14, LogLevel.Information, "Grain Service {GrainServiceType} started successfully.")]
        public static partial void GrainServiceStarted(ILogger logger, string GrainServiceType);

        [LoggerMessage(15, LogLevel.Debug, "Silo shutdown in progress. Waiting for shutdown to be completed.")]
        public static partial void SiloStopInProgress(ILogger logger);

        [LoggerMessage(16, LogLevel.Debug, "Silo shutdown still in progress...")]
        public static partial void WaitingForSiloStop(ILogger logger);

        [LoggerMessage(17, LogLevel.Debug, "{Message}")]
        public static partial void SiloShuttingDown(ILogger logger, string Message);

        [LoggerMessage(18, LogLevel.Debug, "{Message}")]
        public static partial void SiloShutDown(ILogger logger, string Message);

        [LoggerMessage(19, LogLevel.Error, "Error stopping message center")]
        public static partial void ErrorStoppingMessageCenter(ILogger logger, Exception exception);

        [LoggerMessage(20, LogLevel.Error, "Error deactivating activations.")]
        public static partial void ErrorDeactivatingActivations(ILogger logger, Exception exception);

        [LoggerMessage(21, LogLevel.Warning, "{Message}")]
        public static partial void Warning(ILogger logger, string Message);

        [LoggerMessage(22, LogLevel.Error, "Failed to shutdown gracefully. About to terminate ungracefully")]
        public static partial void SiloFailedToStopMembership(ILogger logger, Exception exception);

        [LoggerMessage(23, LogLevel.Error, "Sending gateway disconnection requests to connected clients failed.")]
        public static partial void ErrorSendingGatewayDisconnectionRequests(ILogger logger, Exception exception);

        [LoggerMessage(24, LogLevel.Error, "Stopping GrainService '{GrainService}' failed.")]
        public static partial void ErrorStoppingGrainService(ILogger logger, Exception exception, GrainService GrainService);

        [LoggerMessage(25, LogLevel.Debug, "{GrainServiceType} Grain Service with Id {GrainServiceId} stopped successfully.")]
        public static partial void GrainServiceStopped(ILogger logger, string GrainServiceType, string GrainServiceId);
    }
}
