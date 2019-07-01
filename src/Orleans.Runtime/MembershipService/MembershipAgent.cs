using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using System.Linq;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for updating membership table with details about the local silo.
    /// </summary>
    internal class MembershipAgent : ILifecycleParticipant<ISiloLifecycle>, IDisposable, MembershipAgent.ITestAccessor
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager tableManager;
        private readonly ClusterHealthMonitor clusterHealthMonitor;
        private readonly ILocalSiloDetails localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly ILogger<MembershipAgent> log;
        private readonly IAsyncTimer iAmAliveTimer;


        public MembershipAgent(
            MembershipTableManager tableManager,
            ClusterHealthMonitor clusterHealthMonitor,
            ILocalSiloDetails localSilo,
            IFatalErrorHandler fatalErrorHandler,
            IOptions<ClusterMembershipOptions> options,
            ILogger<MembershipAgent> log,
            IAsyncTimerFactory timerFactory)
        {
            this.tableManager = tableManager;
            this.clusterHealthMonitor = clusterHealthMonitor;
            this.localSilo = localSilo;
            this.fatalErrorHandler = fatalErrorHandler;
            this.clusterMembershipOptions = options.Value;
            this.log = log;
            this.iAmAliveTimer = timerFactory.Create(
                this.clusterMembershipOptions.IAmAliveTablePublishTimeout,
                nameof(UpdateIAmAlive));
        }

        internal interface ITestAccessor
        {
            Action OnUpdateIAmAlive { get; set; }
        }

        Action ITestAccessor.OnUpdateIAmAlive { get; set; }

        private async Task UpdateIAmAlive()
        {
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting periodic membership liveness timestamp updates");
            try
            {
                TimeSpan? onceOffDelay = default;
                while (await this.iAmAliveTimer.NextTick(onceOffDelay) && !this.tableManager.CurrentStatus.IsTerminating())
                {
                    onceOffDelay = default;

                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        ((ITestAccessor)this).OnUpdateIAmAlive?.Invoke();
                        await this.tableManager.UpdateIAmAlive();
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Updating IAmAlive took {Elapsed}", stopwatch.Elapsed);
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError(
                            (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                            "Failed to update table entry for this silo, will retry shortly: {Exception}",
                            exception);

                        // Retry quickly
                        onceOffDelay = TimeSpan.FromMilliseconds(200);
                    }
                }
            }
            catch (Exception exception)
            {
                this.log.LogError("Error updating liveness timestamp: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(UpdateIAmAlive), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping periodic membership liveness timestamp updates");
            }
        }

        private async Task BecomeActive()
        {
            this.log.LogInformation(
                (int)ErrorCode.MembershipBecomeActive,
                "-BecomeActive");

            if (this.clusterMembershipOptions.ValidateInitialConnectivity)
            {
                await this.ValidateInitialConnectivity();
            }
            else
            {
                this.log.LogWarning(
                      (int)ErrorCode.MembershipSendingPreJoinPing,
                      $"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.ValidateInitialConnectivity)} is set to false. This is NOT recommended for a production environment.");
            }

            try
            {
                await this.UpdateStatus(SiloStatus.Active);
                this.log.LogInformation(
                    (int)ErrorCode.MembershipFinishBecomeActive,
                    "-Finished BecomeActive.");
            }
            catch (Exception exception)
            {
                this.log.LogInformation(
                    (int)ErrorCode.MembershipFailedToBecomeActive,
                    "BecomeActive failed: {Exception}",
                    exception);
                throw;
            }
        }

        private async Task ValidateInitialConnectivity()
        {
            var activeSilos = new List<SiloAddress>();
            var now = DateTime.UtcNow;
            foreach (var item in this.tableManager.MembershipTableSnapshot.Entries)
            {
                var entry = item.Value;
                if (entry.Status != SiloStatus.Active) continue;
                if (entry.SiloAddress.Endpoint.Equals(this.localSilo.SiloAddress.Endpoint)) continue;
                if (entry.HasMissedIAmAlivesSince(this.clusterMembershipOptions, now) != default) continue;

                activeSilos.Add(entry.SiloAddress);
            }

            var failedSilos = await this.clusterHealthMonitor.CheckClusterConnectivity(activeSilos.ToArray());
            var successfulSilos = activeSilos.Where(s => !failedSilos.Contains(s));

            if (failedSilos.Count > 0)
            {
                this.log.LogError(
                    (int)ErrorCode.MembershipJoiningPreconditionFailure,
                    "Failed to get ping responses from {FailedCount} of {ActiveCount} active silos. "
                    + "Newly joining silos validate connectivity with all active silos that have recently updated their 'I Am Alive' value before joining the cluster."
                    + "Successfully contacted: {SuccessfulSilos}. Silos which did not respond successfully are: {FailedSilos}",
                    failedSilos.Count,
                    activeSilos.Count,
                    Utils.EnumerableToString(successfulSilos),
                    Utils.EnumerableToString(failedSilos));

                var msg = $"Failed to get ping responses from {failedSilos.Count} of {activeSilos.Count} active silos. "
                    + "Newly joining silos validate connectivity with all active silos that have recently updated their 'I Am Alive' value before joining the cluster."
                    + $"Successfully contacted: {Utils.EnumerableToString(successfulSilos)}. Failed to get response from: {Utils.EnumerableToString(failedSilos)}";
                throw new OrleansClusterConnectivityCheckFailedException(msg);
            }
        }

        private async Task StartJoining()
        {
            this.log.Info(ErrorCode.MembershipJoining, "-Joining");
            try
            {
                await this.UpdateStatus(SiloStatus.Joining);
            }
            catch (Exception exc)
            {
                this.log.Error(ErrorCode.MembershipFailedToJoin, "Error updating status to Joining", exc);
                throw;
            }
        }

        private async Task Shutdown()
        {
            this.log.Info(ErrorCode.MembershipShutDown, "-Shutdown");
            try
            {
                await this.UpdateStatus(SiloStatus.ShuttingDown);
            }
            catch (Exception exc)
            {
                this.log.Error(ErrorCode.MembershipFailedToShutdown, "Error updating status to ShuttingDown", exc);
                throw;
            }
        }

        private async Task Stop()
        {
            log.Info(ErrorCode.MembershipStop, "-Stop");
            try
            {
                await this.UpdateStatus(SiloStatus.Stopping);
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.MembershipFailedToStop, "Error updating status to Stopping", exc);
                throw;
            }
        }

        private async Task KillMyself()
        {
            this.log.LogInformation(
                (int)ErrorCode.MembershipKillMyself,
                "Updating status to " + nameof(SiloStatus.Dead));

            try
            {
                await this.UpdateStatus(SiloStatus.Dead);
            }
            catch (Exception exception)
            {
                this.log.LogError(
                    (int)ErrorCode.MembershipFailedToKillMyself,
                    "Failure updating status to " + nameof(SiloStatus.Dead) + ": {Exception}",
                    exception);
                throw;
            }
        }

        private async Task UpdateStatus(SiloStatus status)
        {
            await this.tableManager.UpdateStatus(status);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            {
                Task OnRuntimeInitializeStart(CancellationToken ct)
                {
                    return Task.CompletedTask;
                }

                async Task OnRuntimeInitializeStop(CancellationToken ct)
                {
                    this.iAmAliveTimer.Dispose();
                    this.cancellation.Cancel();
                    await Task.WhenAny(
                        Task.Run(() => this.KillMyself()),
                        Task.Delay(TimeSpan.FromMinutes(1)));
                }

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.RuntimeInitialize,
                    OnRuntimeInitializeStart,
                    OnRuntimeInitializeStop);
            }

            {
                async Task OnBecomeJoiningStart(CancellationToken ct)
                {
                    await Task.Run(() => this.StartJoining());
                }

                async Task OnBecomeJoiningStop(CancellationToken ct)
                {
                    await Task.WhenAny(
                        Task.Run(() => this.KillMyself()),
                        Task.Delay(TimeSpan.FromMinutes(1)),
                        ct.WhenCancelled());
                }

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.BecomeJoining,
                    OnBecomeJoiningStart,
                    OnBecomeJoiningStop);
            }

            {
                var tasks = new List<Task>();

                async Task OnBecomeActiveStart(CancellationToken ct)
                {
                    await Task.Run(() => this.BecomeActive());
                    tasks.Add(Task.Run(() => this.UpdateIAmAlive()));
                }

                async Task OnBecomeActiveStop(CancellationToken ct)
                {
                    this.iAmAliveTimer.Dispose();
                    this.cancellation.Cancel(throwOnFirstException: false);
                    var cancellationTask = ct.WhenCancelled();

                    if (ct.IsCancellationRequested)
                    {
                        await Task.Run(() => this.Stop());
                    }
                    else
                    {
                        var task = await Task.WhenAny(cancellationTask, this.Shutdown());
                        if (ReferenceEquals(task, cancellationTask))
                        {
                            this.log.LogWarning("Graceful shutdown aborted: starting ungraceful shutdown");
                            await Task.Run(() => this.Stop());
                        }
                        else
                        {
                            await Task.WhenAny(cancellationTask, Task.WhenAll(tasks));
                        }
                    }
                }

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.BecomeActive,
                    OnBecomeActiveStart,
                    OnBecomeActiveStop);
            }
        }

        public void Dispose()
        {
            this.iAmAliveTimer.Dispose();
        }
    }
}
