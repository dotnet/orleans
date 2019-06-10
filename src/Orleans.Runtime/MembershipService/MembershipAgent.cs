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
    internal class MembershipAgent : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager tableManager;
        private readonly ClusterHealthMonitor clusterHealthMonitor;
        private readonly ILocalSiloDetails localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly ILogger<MembershipAgent> log;
        private readonly int updateLivenessPeriodMilliseconds;

        public MembershipAgent(
            MembershipTableManager tableManager,
            ClusterHealthMonitor clusterHealthMonitor,
            ILocalSiloDetails localSilo,
            IFatalErrorHandler fatalErrorHandler,
            IOptions<ClusterMembershipOptions> options,
            ILogger<MembershipAgent> log)
        {
            this.tableManager = tableManager;
            this.clusterHealthMonitor = clusterHealthMonitor;
            this.localSilo = localSilo;
            this.fatalErrorHandler = fatalErrorHandler;
            this.clusterMembershipOptions = options.Value;
            this.updateLivenessPeriodMilliseconds = (int)this.clusterMembershipOptions.IAmAliveTablePublishTimeout.TotalMilliseconds;
            this.log = log;
        }
        
        private async Task ProcessMembershipUpdates()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();
            var current = this.tableManager.MembershipTableUpdates;

            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = current.NextAsync();

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (this.tableManager.CurrentStatus.IsTerminating() || ReferenceEquals(task, cancellationTask)) break;

                    current = next.GetAwaiter().GetResult();

                    if (!current.HasValue)
                    {
                        this.log.LogWarning("Received a membership update with no data");
                        continue;
                    }

                    var snapshot = current.Value;
                    if (!snapshot.Entries.TryGetValue(this.localSilo.SiloAddress, out var entry))
                    {
                        //throw new OrleansMissingMembershipEntryException();
                        continue;
                    }

                    // Check to see if this silo has been declared dead.
                    if (entry.Status == SiloStatus.Dead && !this.tableManager.CurrentStatus.IsTerminating())
                    {
                        var message = $"{OrleansSiloDeclaredDeadException.BaseMessage} Membership record: {entry.ToFullString()}";
                        this.log.LogError((int)ErrorCode.MembershipKillMyselfLocally, message);
                        throw new OrleansSiloDeclaredDeadException(message);
                    }
                }
            }
            catch (OrleansSiloDeclaredDeadException exception)
            {
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            catch (Exception exception)
            {
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping membership update processor");
            }
        }

        private async Task UpdateIAmAlive()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();

            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting periodic membership liveness timestamp updates");
            try
            {
                var delayMilliseconds = this.updateLivenessPeriodMilliseconds;
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = Task.Delay(delayMilliseconds);

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (this.tableManager.CurrentStatus.IsTerminating() || ReferenceEquals(task, cancellationTask)) break;

                    var snapshot = this.tableManager.MembershipTableSnapshot;

                    if (!snapshot.Entries.TryGetValue(this.localSilo.SiloAddress, out var entry))
                    {
                        throw new OrleansMissingMembershipEntryException();
                    }

                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        await this.tableManager.UpdateIAmAlive();
                        stopwatch.Stop();
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Updating liveness for entry {Entry} took {Elapsed}", entry, stopwatch.Elapsed);
                        delayMilliseconds = Math.Max(this.updateLivenessPeriodMilliseconds - (int)stopwatch.Elapsed.TotalMilliseconds, 0);
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError(
                            (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                            "Failed to update table entry for this silo, will retry shortly: {Exception}",
                            exception);

                        // Retry quickly
                        delayMilliseconds = 200;
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
                this.fatalErrorHandler.OnFatalException(this, nameof(ValidateInitialConnectivity), new OrleansClusterConnectivityCheckFailedException(msg));
            }
        }

        private async Task StartJoining()
        {
            this.log.Info(ErrorCode.MembershipShutDown, "-" + "Shutdown");
            try
            {
                await this.UpdateStatus(SiloStatus.Joining);
            }
            catch (Exception exc)
            {
                this.log.Error(ErrorCode.MembershipFailedToShutdown, "Error doing Shutdown", exc);
                throw;
            }
        }

        private async Task Shutdown()
        {
            this.log.Info(ErrorCode.MembershipShutDown, "-" + "Shutdown");
            try
            {
                await this.UpdateStatus(SiloStatus.ShuttingDown);
            }
            catch (Exception exc)
            {
                this.log.Error(ErrorCode.MembershipFailedToShutdown, "Error doing Shutdown", exc);
                throw;
            }
        }

        private async Task Stop()
        {
            const string operation = "Stop";
            log.Info(ErrorCode.MembershipStop, "-" + operation);
            try
            {
                await this.UpdateStatus(SiloStatus.Stopping);
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.MembershipFailedToStop, "Error doing " + operation, exc);
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
                var tasks = new List<Task>();

                Task OnRuntimeInitializeStart(CancellationToken ct)
                {
                    tasks.Add(Task.Run(() => this.ProcessMembershipUpdates()));
                    return Task.CompletedTask;
                }

                async Task OnRuntimeInitializeStop(CancellationToken ct)
                {
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
                async Task AfterRuntimeGrainServicesStart(CancellationToken ct)
                {
                    await Task.Run(() => this.StartJoining());
                }

                Task AfterRuntimeGrainServicesStop(CancellationToken ct) => Task.CompletedTask;

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.RuntimeGrainServices + 1,
                    AfterRuntimeGrainServicesStart,
                    AfterRuntimeGrainServicesStop);
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
                    this.cancellation.Cancel(throwOnFirstException: false);
                    var cancellationTask = ct.WhenCancelled();
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

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.BecomeActive,
                    OnBecomeActiveStart,
                    OnBecomeActiveStop);
            }
        }
    }
}
