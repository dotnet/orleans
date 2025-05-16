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
    internal partial class MembershipAgent : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable, MembershipAgent.ITestAccessor
    {
        private static readonly TimeSpan EXP_BACKOFF_CONTENTION_MIN = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan EXP_BACKOFF_CONTENTION_MAX = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromMilliseconds(1000);
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
            LogDebugStartingPeriodicMembershipLivenessTimestampUpdates();
            try
            {
                // jitter for initial
                TimeSpan? overrideDelayPeriod = RandomTimeSpan.Next(this.clusterMembershipOptions.IAmAliveTablePublishTimeout);
                var exponentialBackoff = new ExponentialBackoff(EXP_BACKOFF_CONTENTION_MIN, EXP_BACKOFF_CONTENTION_MAX, EXP_BACKOFF_STEP);
                var runningFailures = 0;
                while (await this.iAmAliveTimer.NextTick(overrideDelayPeriod) && !this.tableManager.CurrentStatus.IsTerminating())
                {
                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        ((ITestAccessor)this).OnUpdateIAmAlive?.Invoke();
                        await this.tableManager.UpdateIAmAlive();
                        LogTraceUpdatingIAmAliveTook(stopwatch.Elapsed);
                        overrideDelayPeriod = default;
                        runningFailures = 0;
                    }
                    catch (Exception exception)
                    {
                        runningFailures += 1;
                        LogWarningFailedToUpdateTableEntryForThisSilo(exception);
                        // Retry quickly and then exponentially back off
                        overrideDelayPeriod = exponentialBackoff.Next(runningFailures);
                    }
                }
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                LogErrorErrorUpdatingLivenessTimestamp(exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(UpdateIAmAlive), exception);
            }
            finally
            {
                LogDebugStoppingPeriodicMembershipLivenessTimestampUpdates();
            }
        }

        private async Task BecomeActive()
        {
            LogInformationBecomeActive();
            await this.ValidateInitialConnectivity();

            try
            {
                await this.UpdateStatus(SiloStatus.Active);
                LogInformationFinishedBecomeActive();
            }
            catch (Exception exception)
            {
                LogInformationBecomeActiveFailed(exception);
                throw;
            }
        }

        private async Task ValidateInitialConnectivity()
        {
            // Continue attempting to validate connectivity until some reasonable timeout.
            var maxAttemptTime = this.clusterMembershipOptions.MaxJoinAttemptTime;
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
                        if (entry.HasMissedIAmAlives(this.clusterMembershipOptions, now) != default) continue;

                        activeSilos.Add(entry.SiloAddress);
                    }

                    var failedSilos = await CheckClusterConnectivity(activeSilos.ToArray());
                    var successfulSilos = activeSilos.Where(s => !failedSilos.Contains(s)).ToList();

                    // If there were no failures, terminate the loop and return without error.
                    if (failedSilos.Count == 0) break;

                    LogErrorFailedToGetPingResponses(failedSilos.Count, activeSilos.Count, new(successfulSilos), new(failedSilos), attemptUntil, attemptNumber);

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
                    LogErrorFailedToValidateInitialClusterConnectivity(exception);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                ++attemptNumber;
                now = this.getUtcDateTime();
            }

            async Task<List<SiloAddress>> CheckClusterConnectivity(SiloAddress[] members)
            {
                if (members.Length == 0) return new List<SiloAddress>();

                var tasks = new List<Task<bool>>(members.Length);

                LogInformationAboutToSendPings(members.Length, new EnumerableToStringLogValue<SiloAddress>(members));

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
                    await siloProber.Probe(silo, 0).WaitAsync(timeout);
                    return true;
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                LogWarningDidNotReceiveProbeResponse(log, exception, silo, timeout);
                return false;
            }
        }

        private async Task BecomeJoining()
        {
            LogInformationJoining();
            try
            {
                await this.UpdateStatus(SiloStatus.Joining);
            }
            catch (Exception exc)
            {
                LogErrorErrorUpdatingStatusToJoining(exc);
                throw;
            }
        }

        private async Task BecomeShuttingDown()
        {
            LogDebugShutdown();

            try
            {
                await this.UpdateStatus(SiloStatus.ShuttingDown);
            }
            catch (Exception exc)
            {
                LogErrorErrorUpdatingStatusToShuttingDown(exc);
                throw;
            }
        }

        private async Task BecomeStopping()
        {
            LogDebugStop();

            try
            {
                await this.UpdateStatus(SiloStatus.Stopping);
            }
            catch (Exception exc)
            {
                LogErrorErrorUpdatingStatusToStopping(exc);
                throw;
            }
        }

        private async Task BecomeDead()
        {
            LogDebugUpdatingStatusToDead();

            try
            {
                await this.UpdateStatus(SiloStatus.Dead);
            }
            catch (Exception exception)
            {
                LogErrorFailureUpdatingStatusToDead(exception);
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

        private readonly struct EnumerableToStringLogValue<T>(IEnumerable<T> enumerable)
        {
            public override string ToString() => Utils.EnumerableToString(enumerable);
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting periodic membership liveness timestamp updates"
        )]
        private partial void LogDebugStartingPeriodicMembershipLivenessTimestampUpdates();

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Updating IAmAlive took {Elapsed}"
        )]
        private partial void LogTraceUpdatingIAmAliveTook(TimeSpan elapsed);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipUpdateIAmAliveFailure,
            Level = LogLevel.Warning,
            Message = "Failed to update table entry for this silo, will retry shortly"
        )]
        private partial void LogWarningFailedToUpdateTableEntryForThisSilo(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Stopping periodic membership liveness timestamp updates"
        )]
        private partial void LogDebugStoppingPeriodicMembershipLivenessTimestampUpdates();

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error updating liveness timestamp"
        )]
        private partial void LogErrorErrorUpdatingLivenessTimestamp(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipBecomeActive,
            Level = LogLevel.Information,
            Message = "-BecomeActive"
        )]
        private partial void LogInformationBecomeActive();

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFinishBecomeActive,
            Level = LogLevel.Information,
            Message = "-Finished BecomeActive."
        )]
        private partial void LogInformationFinishedBecomeActive();

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFailedToBecomeActive,
            Level = LogLevel.Information,
            Message = "BecomeActive failed"
        )]
        private partial void LogInformationBecomeActiveFailed(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipJoiningPreconditionFailure,
            Level = LogLevel.Error,
            Message = "Failed to get ping responses from {FailedCount} of {ActiveCount} active silos. " +
                      "Newly joining silos validate connectivity with all active silos that have recently updated their 'I Am Alive' value before joining the cluster. " +
                      "Successfully contacted: {SuccessfulSilos}. Silos which did not respond successfully are: {FailedSilos}. " +
                      "Will continue attempting to validate connectivity until {Timeout}. Attempt #{Attempt}"
        )]
        private partial void LogErrorFailedToGetPingResponses(int failedCount, int activeCount, EnumerableToStringLogValue<SiloAddress> successfulSilos, EnumerableToStringLogValue<SiloAddress> failedSilos, DateTime timeout, int attempt);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to validate initial cluster connectivity"
        )]
        private partial void LogErrorFailedToValidateInitialClusterConnectivity(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipSendingPreJoinPing,
            Level = LogLevel.Information,
            Message = "About to send pings to {Count} nodes in order to validate communication in the Joining state. Pinged nodes = {Nodes}"
        )]
        private partial void LogInformationAboutToSendPings(int count, EnumerableToStringLogValue<SiloAddress> nodes);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Did not receive a probe response from silo {SiloAddress} in timeout {Timeout}"
        )]
        private static partial void LogWarningDidNotReceiveProbeResponse(ILogger logger, Exception exception, SiloAddress siloAddress, TimeSpan timeout);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipJoining,
            Level = LogLevel.Information,
            Message = "Joining"
        )]
        private partial void LogInformationJoining();

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFailedToJoin,
            Level = LogLevel.Error,
            Message = "Error updating status to Joining"
        )]
        private partial void LogErrorErrorUpdatingStatusToJoining(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipShutDown,
            Level = LogLevel.Debug,
            Message = "-Shutdown"
        )]
        private partial void LogDebugShutdown();

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFailedToShutdown,
            Level = LogLevel.Error,
            Message = "Error updating status to ShuttingDown"
        )]
        private partial void LogErrorErrorUpdatingStatusToShuttingDown(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipStop,
            Level = LogLevel.Debug,
            Message = "-Stop"
        )]
        private partial void LogDebugStop();

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFailedToStop,
            Level = LogLevel.Error,
            Message = "Error updating status to Stopping"
        )]
        private partial void LogErrorErrorUpdatingStatusToStopping(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipKillMyself,
            Level = LogLevel.Debug,
            Message = "Updating status to Dead"
        )]
        private partial void LogDebugUpdatingStatusToDead();

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFailedToKillMyself,
            Level = LogLevel.Error,
            Message = "Failure updating status to Dead"
        )]
        private partial void LogErrorFailureUpdatingStatusToDead(Exception exception);
    }
}
