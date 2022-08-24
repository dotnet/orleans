using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using System.Linq;
using Orleans.Internal;
using System.Reflection.Metadata;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for updating membership table with details about the local silo.
    /// </summary>
    internal class MembershipAgent : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable, MembershipAgent.ITestAccessor
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager tableManager;
        private readonly ILocalSiloDetails localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly ILogger<MembershipAgent> log;
        private readonly IRemoteSiloProber siloProber;
        private readonly IAsyncTimer iAmAliveTimer;
        private Func<DateTime> getUtcDateTime = () => DateTime.UtcNow;

        public MembershipAgent(
            MembershipTableManager tableManager,
            ILocalSiloDetails localSilo,
            IFatalErrorHandler fatalErrorHandler,
            IOptions<ClusterMembershipOptions> options,
            ILogger<MembershipAgent> log,
            IAsyncTimerFactory timerFactory,
            IRemoteSiloProber siloProber)
        {
            this.tableManager = tableManager;
            this.localSilo = localSilo;
            this.fatalErrorHandler = fatalErrorHandler;
            this.clusterMembershipOptions = options.Value;
            this.log = log;
            this.siloProber = siloProber;
            this.iAmAliveTimer = timerFactory.Create(
                this.clusterMembershipOptions.IAmAliveTablePublishTimeout,
                nameof(UpdateIAmAlive));
        }

        internal interface ITestAccessor
        {
            Action OnUpdateIAmAlive { get; set; }
            Func<DateTime> GetDateTime { get; set; }
        }

        Action ITestAccessor.OnUpdateIAmAlive { get; set; }
        Func<DateTime> ITestAccessor.GetDateTime { get => this.getUtcDateTime; set => this.getUtcDateTime = value ?? throw new ArgumentNullException(nameof(value)); }

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
                            exception,
                            "Failed to update table entry for this silo, will retry shortly");

                        // Retry quickly
                        onceOffDelay = TimeSpan.FromMilliseconds(200);
                    }
                }
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                this.log.LogError(exception, "Error updating liveness timestamp");
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

            await this.ValidateInitialConnectivity();

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
                    exception,
                    "BecomeActive failed");
                throw;
            }
        }

        private async Task ValidateInitialConnectivity()
        {
            // Continue attempting to validate connectivity until some reasonable timeout.
            var maxAttemptTime = this.clusterMembershipOptions.ProbeTimeout.Multiply(5.0 * this.clusterMembershipOptions.NumMissedProbesLimit);
            var attemptNumber = 1;
            var now = this.getUtcDateTime();
            var attemptUntil = now + maxAttemptTime;
            var canContinue = true;

            while (true)
            {
                try
                {
                    var activeSilos = new List<SiloAddress>();
                    foreach (var item in this.tableManager.MembershipTableSnapshot.Entries)
                    {
                        var entry = item.Value;
                        if (entry.Status != SiloStatus.Active) continue;
                        if (entry.SiloAddress.IsSameLogicalSilo(this.localSilo.SiloAddress)) continue;
                        if (entry.HasMissedIAmAlivesSince(this.clusterMembershipOptions, now) != default) continue;

                        activeSilos.Add(entry.SiloAddress);
                    }

                    var failedSilos = await CheckClusterConnectivity(activeSilos.ToArray());
                    var successfulSilos = activeSilos.Where(s => !failedSilos.Contains(s)).ToList();

                    // If there were no failures, terminate the loop and return without error.
                    if (failedSilos.Count == 0) break;

                    this.log.LogError(
                        (int)ErrorCode.MembershipJoiningPreconditionFailure,
                        "Failed to get ping responses from {FailedCount} of {ActiveCount} active silos. "
                        + "Newly joining silos validate connectivity with all active silos that have recently updated their 'I Am Alive' value before joining the cluster. "
                        + "Successfully contacted: {SuccessfulSilos}. Silos which did not respond successfully are: {FailedSilos}. "
                        + "Will continue attempting to validate connectivity until {Timeout}. Attempt #{Attempt}",
                        failedSilos.Count,
                        activeSilos.Count,
                        Utils.EnumerableToString(successfulSilos),
                        Utils.EnumerableToString(failedSilos),
                        attemptUntil,
                        attemptNumber);

                    if (now + TimeSpan.FromSeconds(5) > attemptUntil)
                    {
                        canContinue = false;
                        var msg = $"Failed to get ping responses from {failedSilos.Count} of {activeSilos.Count} active silos. "
                            + "Newly joining silos validate connectivity with all active silos that have recently updated their 'I Am Alive' value before joining the cluster. "
                            + $"Successfully contacted: {Utils.EnumerableToString(successfulSilos)}. Failed to get response from: {Utils.EnumerableToString(failedSilos)}";
                        throw new OrleansClusterConnectivityCheckFailedException(msg);
                    }

                    // Refresh membership after some delay and retry.
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await this.tableManager.Refresh();
                }
                catch (Exception exception) when (canContinue)
                {
                    this.log.LogError(exception, "Failed to validate initial cluster connectivity");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                ++attemptNumber;
                now = this.getUtcDateTime();
            }

            async Task<List<SiloAddress>> CheckClusterConnectivity(SiloAddress[] members)
            {
                if (members.Length == 0) return new List<SiloAddress>();

                var tasks = new List<Task<bool>>(members.Length);

                this.log.LogInformation(
                    (int)ErrorCode.MembershipSendingPreJoinPing,
                    "About to send pings to {Count} nodes in order to validate communication in the Joining state. Pinged nodes = {Nodes}",
                    members.Length,
                    Utils.EnumerableToString(members));

                var timeout = this.clusterMembershipOptions.ProbeTimeout;
                foreach (var silo in members)
                {
                    tasks.Add(ProbeSilo(this.siloProber, silo, timeout, this.log));
                }

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    // Ignore exceptions for now.
                }

                var failed = new List<SiloAddress>();
                for (var i = 0; i < tasks.Count; i++)
                {
                    if (tasks[i].Status != TaskStatus.RanToCompletion || !tasks[i].GetAwaiter().GetResult())
                    {
                        failed.Add(members[i]);
                    }
                }

                return failed;
            }

            static async Task<bool> ProbeSilo(IRemoteSiloProber siloProber, SiloAddress silo, TimeSpan timeout, ILogger log)
            {
                Exception exception;
                try
                {
                    using var cancellation = new CancellationTokenSource(timeout);
                    var probeTask = siloProber.Probe(silo, 0);
                    var cancellationTask = cancellation.Token.WhenCancelled();
                    var completedTask = await Task.WhenAny(probeTask, cancellationTask).ConfigureAwait(false);

                    if (ReferenceEquals(completedTask, probeTask))
                    {
                        cancellation.Cancel();
                        if (probeTask.IsFaulted)
                        {
                            exception = probeTask.Exception;
                        }
                        else if (probeTask.Status == TaskStatus.RanToCompletion)
                        {
                            return true;
                        }
                        else
                        {
                            exception = null;
                        }
                    }
                    else
                    {
                        exception = null;
                    }
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                log.LogWarning(exception, "Did not receive a probe response from silo {SiloAddress} in timeout {Timeout}", silo.ToString(), timeout);
                return false;
            }
        }

        private async Task BecomeJoining()
        {
            this.log.LogInformation((int)ErrorCode.MembershipJoining, "Joining");
            try
            {
                await this.UpdateStatus(SiloStatus.Joining);
            }
            catch (Exception exc)
            {
                this.log.LogError(
                    (int)ErrorCode.MembershipFailedToJoin,
                    exc,
                    "Error updating status to Joining");
                throw;
            }
        }

        private async Task BecomeShuttingDown()
        {
            if (this.log.IsEnabled(LogLevel.Debug))
            {
                this.log.LogDebug((int)ErrorCode.MembershipShutDown, "-Shutdown");
            }
            
            try
            {
                await this.UpdateStatus(SiloStatus.ShuttingDown);
            }
            catch (Exception exc)
            {
                this.log.LogError((int)ErrorCode.MembershipFailedToShutdown, exc, "Error updating status to ShuttingDown");
                throw;
            }
        }

        private async Task BecomeStopping()
        {
            if (this.log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug((int)ErrorCode.MembershipStop, "-Stop");
            }

            try
            {
                await this.UpdateStatus(SiloStatus.Stopping);
            }
            catch (Exception exc)
            {
                log.LogError((int)ErrorCode.MembershipFailedToStop, exc, "Error updating status to Stopping");
                throw;
            }
        }

        private async Task BecomeDead()
        {
            if (this.log.IsEnabled(LogLevel.Debug))
            {
                this.log.LogDebug(
                   (int)ErrorCode.MembershipKillMyself,
                    "Updating status to Dead");
            }

            try
            {
                await this.UpdateStatus(SiloStatus.Dead);
            }
            catch (Exception exception)
            {
                this.log.LogError(
                    (int)ErrorCode.MembershipFailedToKillMyself,
                    exception,
                    "Failure updating status to " + nameof(SiloStatus.Dead));
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
                Task OnRuntimeInitializeStart(CancellationToken ct) => Task.CompletedTask;

                async Task OnRuntimeInitializeStop(CancellationToken ct)
                {
                    this.iAmAliveTimer.Dispose();
                    this.cancellation.Cancel();
                    await Task.WhenAny(
                        Task.Run(() => this.BecomeDead()),
                        Task.Delay(TimeSpan.FromMinutes(1)));
                }

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.RuntimeInitialize + 1, // Gossip before the outbound queue gets closed
                    OnRuntimeInitializeStart,
                    OnRuntimeInitializeStop);
            }

            {
                async Task AfterRuntimeGrainServicesStart(CancellationToken ct)
                {
                    await Task.Run(() => this.BecomeJoining());
                }

                Task AfterRuntimeGrainServicesStop(CancellationToken ct) => Task.CompletedTask;

                lifecycle.Subscribe(
                    nameof(MembershipAgent),
                    ServiceLifecycleStage.AfterRuntimeGrainServices,
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
                    this.iAmAliveTimer.Dispose();
                    this.cancellation.Cancel(throwOnFirstException: false);
                    var cancellationTask = ct.WhenCancelled();

                    if (ct.IsCancellationRequested)
                    {
                        await Task.Run(() => this.BecomeStopping());
                    }
                    else
                    {
                        // Allow some minimum time for graceful shutdown.
                        var gracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod), cancellationTask);
                        var task = await Task.WhenAny(gracePeriod, this.BecomeShuttingDown());
                        if (ReferenceEquals(task, gracePeriod))
                        {
                            this.log.LogWarning("Graceful shutdown aborted: starting ungraceful shutdown");
                            await Task.Run(() => this.BecomeStopping());
                        }
                        else
                        {
                            await Task.WhenAny(gracePeriod, Task.WhenAll(tasks));
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

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, out string reason) => this.iAmAliveTimer.CheckHealth(lastCheckTime, out reason);
    }
}
