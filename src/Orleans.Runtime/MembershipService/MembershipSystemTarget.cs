using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.MembershipService
{
    internal sealed partial class MembershipSystemTarget : SystemTarget, IMembershipService, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IMembershipManager membershipManager;
        private readonly ILogger<MembershipSystemTarget> log;
        private readonly IInternalGrainFactory grainFactory;
        private readonly MessagingInstruments _messagingInstruments;
        private readonly TimeProvider _timeProvider;

        public MembershipSystemTarget(
            IMembershipManager membershipManager,
            ILogger<MembershipSystemTarget> log,
            IInternalGrainFactory grainFactory,
            MessagingInstruments messagingInstruments,
            SystemTargetShared shared,
            [FromKeyedServices(TimeProviderNames.Membership)] TimeProvider timeProvider)
            : base(Constants.MembershipServiceType, shared)
        {
            this.membershipManager = membershipManager;
            this.log = log;
            this.grainFactory = grainFactory;
            _messagingInstruments = messagingInstruments;
            _timeProvider = timeProvider;
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public Task Ping(int pingNumber, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task MembershipChangeNotification(
            MembershipTableSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Version != MembershipVersion.MinValue)
            {
                await this.membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
            }
            else
            {
                LogTraceReceivedGossipMembershipChangeNotificationWithMinValue(this.log);
                await ReadTable(cancellationToken);
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

        public async Task<IndirectProbeResponse> ProbeIndirectly(
            SiloAddress target,
            TimeSpan probeTimeout,
            int probeNumber,
            CancellationToken cancellationToken = default)
        {
            var probeTimer = TimeProviderValueStopwatch.StartNew(_timeProvider);
            var succeeded = false;
            string? failureMessage = null;
            try
            {
                var probeTask = this.ProbeInternal(target, probeNumber);
                try
                {
                    await probeTask.WaitAsync(probeTimeout, _timeProvider, cancellationToken);
                }
                catch (TimeoutException exception)
                {
                    LogWarningRequestedProbeTimeoutExceeded(this.log, exception, probeTimeout);
                    throw;
                }

                succeeded = true;
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (Exception exception)
            {
                failureMessage = $"Encountered exception {LogFormatter.PrintException(exception)}";
            }

            var probeResponseTime = probeTimer.GetElapsedTime(out var probeEndTimestamp);
            var healthScore = this.ActivationServices.GetRequiredService<ILocalSiloHealthMonitor>()
                .GetLocalHealthStatus(probeTimer.StartTimestamp, probeEndTimestamp, LocalSiloHealthCheckCategory.Local)
                .Score;
            return new IndirectProbeResponse
            {
                Succeeded = succeeded,
                IntermediaryHealthScore = healthScore,
                FailureMessage = failureMessage,
                ProbeResponseTime = probeResponseTime,
            };
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
                await remoteOracle.MembershipChangeNotification(snapshot, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogWarningErrorSendingGossipNotificationToRemoteSilo(this.log, exception, silo);
            }
        }

        private async Task ReadTable(CancellationToken cancellationToken)
        {
            try
            {
                await this.membershipManager.Refresh(null, cancellationToken);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                throw;
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
                _messagingInstruments.OnPingSend(remoteSilo);
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
