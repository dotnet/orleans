using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.MembershipService
{
    internal sealed partial class MembershipSystemTarget : SystemTarget, IMembershipService, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly MembershipTableManager membershipTableManager;
        private readonly ILogger<MembershipSystemTarget> log;
        private readonly IInternalGrainFactory grainFactory;

        public MembershipSystemTarget(
            MembershipTableManager membershipTableManager,
            ILogger<MembershipSystemTarget> log,
            IInternalGrainFactory grainFactory,
            SystemTargetShared shared)
            : base(Constants.MembershipServiceType, shared)
        {
            this.membershipTableManager = membershipTableManager;
            this.log = log;
            this.grainFactory = grainFactory;
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public Task Ping(int pingNumber) => Task.CompletedTask;

        public async Task MembershipChangeNotification(MembershipTableSnapshot snapshot)
        {
            if (snapshot.Version != MembershipVersion.MinValue)
            {
                await this.membershipTableManager.RefreshFromSnapshot(snapshot);
            }
            else
            {
                LogTraceReceivedGossipMembershipChangeNotificationWithMinValue(this.log);
                await ReadTable();
            }
        }

        /// <summary>
        /// Send a ping to a remote silo. This is intended to be called from a <see cref="SiloHealthMonitor"/>
        /// in order to initiate the call from the <see cref="MembershipSystemTarget"/>'s context
        /// </summary>
        /// <param name="remoteSilo">The remote silo to ping.</param>
        /// <param name="probeNumber">The probe number, for diagnostic purposes.</param>
        /// <returns>The result of pinging the remote silo.</returns>
        public Task ProbeRemoteSilo(SiloAddress remoteSilo, int probeNumber) => this.RunOrQueueTask(() => ProbeInternal(remoteSilo, probeNumber));

        /// <summary>
        /// Send a ping to a remote silo via an intermediary silo. This is intended to be called from a <see cref="SiloHealthMonitor"/>
        /// in order to initiate the call from the <see cref="MembershipSystemTarget"/>'s context
        /// </summary>
        /// <param name="intermediary">The intermediary which will directly probe the target.</param>
        /// <param name="target">The target which will be probed.</param>
        /// <param name="probeTimeout">The timeout for the eventual direct probe request.</param>
        /// <param name="probeNumber">The probe number, for diagnostic purposes.</param>
        /// <returns>The result of pinging the remote silo.</returns>
        public Task<IndirectProbeResponse> ProbeRemoteSiloIndirectly(SiloAddress intermediary, SiloAddress target, TimeSpan probeTimeout, int probeNumber)
        {
            Task<IndirectProbeResponse> ProbeIndirectly()
            {
                var remoteOracle = this.grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, intermediary);
                return remoteOracle.ProbeIndirectly(target, probeTimeout, probeNumber);
            }

            var workItem = new AsyncClosureWorkItem<IndirectProbeResponse>(ProbeIndirectly, this);
            WorkItemGroup.QueueWorkItem(workItem);
            return workItem.Task;
        }

        public async Task<IndirectProbeResponse> ProbeIndirectly(SiloAddress target, TimeSpan probeTimeout, int probeNumber)
        {
            IndirectProbeResponse result;
            var healthScore = this.ActivationServices.GetRequiredService<LocalSiloHealthMonitor>().GetLocalHealthDegradationScore(DateTime.UtcNow);
            var probeResponseTimer = ValueStopwatch.StartNew();
            try
            {
                var probeTask = this.ProbeInternal(target, probeNumber);
                try
                {
                    await probeTask.WaitAsync(probeTimeout);
                }
                catch (TimeoutException exception)
                {
                    LogWarningRequestedProbeTimeoutExceeded(this.log, exception, probeTimeout);
                    throw;
                }

                result = new IndirectProbeResponse
                {
                    Succeeded = true,
                    IntermediaryHealthScore = healthScore,
                    ProbeResponseTime = probeResponseTimer.Elapsed,
                };
            }
            catch (Exception exception)
            {
                result = new IndirectProbeResponse
                {
                    Succeeded = false,
                    IntermediaryHealthScore = healthScore,
                    FailureMessage = $"Encountered exception {LogFormatter.PrintException(exception)}",
                    ProbeResponseTime = probeResponseTimer.Elapsed,
                };
            }

            return result;
        }

        public Task GossipToRemoteSilos(
            List<SiloAddress> gossipPartners,
            MembershipTableSnapshot snapshot,
            SiloAddress updatedSilo,
            SiloStatus updatedStatus)
        {
            async Task Gossip()
            {
                var tasks = new List<Task>(gossipPartners.Count);
                foreach (var silo in gossipPartners)
                {
                    tasks.Add(this.GossipToRemoteSilo(silo, snapshot, updatedSilo, updatedStatus));
                }

                await Task.WhenAll(tasks);
            }

            return this.RunOrQueueTask(Gossip);
        }

        private async Task GossipToRemoteSilo(
            SiloAddress silo,
            MembershipTableSnapshot snapshot,
            SiloAddress updatedSilo,
            SiloStatus updatedStatus)
        {
            LogTraceSendingStatusUpdateGossipNotification(this.log, updatedSilo, updatedStatus, silo);

            try
            {
                var remoteOracle = this.grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, silo);
                await remoteOracle.MembershipChangeNotification(snapshot);
            }
            catch (Exception exception)
            {
                LogWarningErrorSendingGossipNotificationToRemoteSilo(this.log, exception, silo);
            }
        }

        private async Task ReadTable()
        {
            try
            {
                await this.membershipTableManager.Refresh();
            }
            catch (Exception exception)
            {
                LogErrorErrorRefreshingMembershipTable(this.log, exception);
            }
        }

        private Task ProbeInternal(SiloAddress remoteSilo, int probeNumber)
        {
            Task task;
            try
            {
                RequestContext.Set(RequestContext.PING_APPLICATION_HEADER, true);
                var remoteOracle = this.grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, remoteSilo);
                task = remoteOracle.Ping(probeNumber);

                // Update stats counter. Only count Pings that were successfully sent, but not necessarily replied to.
                MessagingInstruments.OnPingSend(remoteSilo);
            }
            finally
            {
                RequestContext.Remove(RequestContext.PING_APPLICATION_HEADER);
            }

            return task;
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            // No-op, just ensure this instance is created at start-up.
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "-Received GOSSIP MembershipChangeNotification with MembershipVersion.MinValue. Going to read the table"
        )]
        private static partial void LogTraceReceivedGossipMembershipChangeNotificationWithMinValue(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Requested probe timeout {ProbeTimeout} exceeded"
        )]
        private static partial void LogWarningRequestedProbeTimeoutExceeded(ILogger logger, Exception exception, TimeSpan probeTimeout);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "-Sending status update GOSSIP notification about silo {UpdatedSilo}, status {UpdatedStatus}, to silo {RemoteSilo}"
        )]
        private static partial void LogTraceSendingStatusUpdateGossipNotification(ILogger logger, SiloAddress updatedSilo, SiloStatus updatedStatus, SiloAddress remoteSilo);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipGossipSendFailure,
            Level = LogLevel.Warning,
            Message = "Error sending gossip notification to remote silo '{Silo}'."
        )]
        private static partial void LogWarningErrorSendingGossipNotificationToRemoteSilo(ILogger logger, Exception exception, SiloAddress silo);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipGossipProcessingFailure,
            Level = LogLevel.Error,
            Message = "Error refreshing membership table."
        )]
        private static partial void LogErrorErrorRefreshingMembershipTable(ILogger logger, Exception exception);
    }
}
