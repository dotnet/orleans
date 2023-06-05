using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using System.Linq;
using Orleans.Internal;

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
            clusterMembershipOptions = options.Value;
            this.log = log;
            this.siloProber = siloProber;
            iAmAliveTimer = timerFactory.Create(
                clusterMembershipOptions.IAmAliveTablePublishTimeout,
                nameof(UpdateIAmAlive));
        }

        internal interface ITestAccessor
        {
            Action OnUpdateIAmAlive { get; set; }
            Func<DateTime> GetDateTime { get; set; }
        }

        Action ITestAccessor.OnUpdateIAmAlive { get; set; }
        Func<DateTime> ITestAccessor.GetDateTime { get => getUtcDateTime; set => getUtcDateTime = value ?? throw new ArgumentNullException(nameof(value)); }

        private async Task UpdateIAmAlive()
        {
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Starting periodic membership liveness timestamp updates");
            try
            {
                TimeSpan? onceOffDelay = default;
                while (await iAmAliveTimer.NextTick(onceOffDelay) && !tableManager.CurrentStatus.IsTerminating())
                {
                    onceOffDelay = default;

                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        ((ITestAccessor)this).OnUpdateIAmAlive?.Invoke();
                        await tableManager.UpdateIAmAlive();
                        if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Updating IAmAlive took {Elapsed}", stopwatch.Elapsed);
                    }
                    catch (Exception exception)
                    {
                        log.LogError(
                            (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                            exception,
                            "Failed to update table entry for this silo, will retry shortly");

                        // Retry quickly
                        onceOffDelay = TimeSpan.FromMilliseconds(200);
                    }
                }
            }
            catch (Exception exception) when (fatalErrorHandler.IsUnexpected(exception))
            {
                log.LogError(exception, "Error updating liveness timestamp");
                fatalErrorHandler.OnFatalException(this, nameof(UpdateIAmAlive), exception);
            }
            finally
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Stopping periodic membership liveness timestamp updates");
            }
        }

        private async Task BecomeActive()
        {
            log.LogInformation(
                (int)ErrorCode.MembershipBecomeActive,
                "-BecomeActive");

            await ValidateInitialConnectivity();

            try
            {
                await UpdateStatus(SiloStatus.Active);
                log.LogInformation(
                    (int)ErrorCode.MembershipFinishBecomeActive,
                    "-Finished BecomeActive.");
            }
            catch (Exception exception)
            {
                log.LogInformation(
                    (int)ErrorCode.MembershipFailedToBecomeActive,
                    exception,
                    "BecomeActive failed");
                throw;
            }
        }

        private async Task ValidateInitialConnectivity()
        {
            // Continue attempting to validate connectivity until some reasonable timeout.
            var maxAttemptTime = clusterMembershipOptions.ProbeTimeout.Multiply(5.0 * clusterMembershipOptions.NumMissedProbesLimit);
            var attemptNumber = 1;
            var now = getUtcDateTime();
            var attemptUntil = now + maxAttemptTime;
            var canContinue = true;

            while (true)
            {
                try
                {
                    var activeSilos = new List<SiloAddress>();
                    foreach (var item in tableManager.MembershipTableSnapshot.Entries)
                    {
                        var entry = item.Value;
                        if (entry.Status != SiloStatus.Active) continue;
                        if (entry.SiloAddress.IsSameLogicalSilo(localSilo.SiloAddress)) continue;
                        if (entry.HasMissedIAmAlivesSince(clusterMembershipOptions, now) != default) continue;

                        activeSilos.Add(entry.SiloAddress);
                    }

                    var failedSilos = await CheckClusterConnectivity(activeSilos.ToArray());
                    var successfulSilos = activeSilos.Where(s => !failedSilos.Contains(s)).ToList();

                    // If there were no failures, terminate the loop and return without error.
                    if (failedSilos.Count == 0) break;

                    log.LogError(
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
                    await tableManager.Refresh();
                }
                catch (Exception exception) when (canContinue)
                {
                    log.LogError(exception, "Failed to validate initial cluster connectivity");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                ++attemptNumber;
                now = getUtcDateTime();
            }

            async Task<List<SiloAddress>> CheckClusterConnectivity(SiloAddress[] members)
            {
                if (members.Length == 0) return new List<SiloAddress>();

                var tasks = new List<Task<bool>>(members.Length);

                log.LogInformation(
                    (int)ErrorCode.MembershipSendingPreJoinPing,
                    "About to send pings to {Count} nodes in order to validate communication in the Joining state. Pinged nodes = {Nodes}",
                    members.Length,
                    Utils.EnumerableToString(members));

                var timeout = clusterMembershipOptions.ProbeTimeout;
                foreach (var silo in members)
                {
                    tasks.Add(ProbeSilo(siloProber, silo, timeout, log));
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
            log.LogInformation((int)ErrorCode.MembershipJoining, "Joining");
            try
            {
                await UpdateStatus(SiloStatus.Joining);
            }
            catch (Exception exc)
            {
                log.LogError(
                    (int)ErrorCode.MembershipFailedToJoin,
                    exc,
                    "Error updating status to Joining");
                throw;
            }
        }

        private async Task BecomeShuttingDown()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug((int)ErrorCode.MembershipShutDown, "-Shutdown");
            }
            
            try
            {
                await UpdateStatus(SiloStatus.ShuttingDown);
            }
            catch (Exception exc)
            {
                log.LogError((int)ErrorCode.MembershipFailedToShutdown, exc, "Error updating status to ShuttingDown");
                throw;
            }
        }

        private async Task BecomeStopping()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug((int)ErrorCode.MembershipStop, "-Stop");
            }

            try
            {
                await UpdateStatus(SiloStatus.Stopping);
            }
            catch (Exception exc)
            {
                log.LogError((int)ErrorCode.MembershipFailedToStop, exc, "Error updating status to Stopping");
                throw;
            }
        }

        private async Task BecomeDead()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug(
                   (int)ErrorCode.MembershipKillMyself,
                    "Updating status to Dead");
            }

            try
            {
                await UpdateStatus(SiloStatus.Dead);
            }
            catch (Exception exception)
            {
                log.LogError(
                    (int)ErrorCode.MembershipFailedToKillMyself,
                    exception,
                    "Failure updating status to " + nameof(SiloStatus.Dead));
                throw;
            }
        }

        private async Task UpdateStatus(SiloStatus status) => await tableManager.UpdateStatus(status);

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            {
                Task OnRuntimeInitializeStart(CancellationToken ct) => Task.CompletedTask;

                async Task OnRuntimeInitializeStop(CancellationToken ct)
                {
                    iAmAliveTimer.Dispose();
                    cancellation.Cancel();
                    await Task.WhenAny(
                        Task.Run(() => BecomeDead()),
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
                    await Task.Run(() => BecomeJoining());
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
                    await Task.Run(() => BecomeActive());
                    tasks.Add(Task.Run(() => UpdateIAmAlive()));
                }

                async Task OnBecomeActiveStop(CancellationToken ct)
                {
                    iAmAliveTimer.Dispose();
                    cancellation.Cancel(throwOnFirstException: false);
                    var cancellationTask = ct.WhenCancelled();

                    if (ct.IsCancellationRequested)
                    {
                        await Task.Run(() => BecomeStopping());
                    }
                    else
                    {
                        // Allow some minimum time for graceful shutdown.
                        var gracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod), cancellationTask);
                        var task = await Task.WhenAny(gracePeriod, BecomeShuttingDown());
                        if (ReferenceEquals(task, gracePeriod))
                        {
                            log.LogWarning("Graceful shutdown aborted: starting ungraceful shutdown");
                            await Task.Run(() => BecomeStopping());
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

        public void Dispose() => iAmAliveTimer.Dispose();

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, out string reason) => iAmAliveTimer.CheckHealth(lastCheckTime, out reason);
    }
}
