using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using static Orleans.Runtime.MembershipService.SiloHealthMonitor;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for monitoring an individual remote silo.
    /// </summary>
    internal class SiloHealthMonitor : ITestAccessor
    {
        private readonly ILogger log;
        private readonly IRemoteSiloProber prober;
        private readonly CancellationTokenSource stoppingCancellation = new CancellationTokenSource();
        private readonly Task stopping;
        private readonly object lockObj = new object();

        /// <summary>
        /// The id of the next probe.
        /// </summary>
        private long nextProbeId;

        /// <summary>
        /// The highest internal probe number which has completed.
        /// </summary>
        private long highestCompletedProbeId = -1;

        /// <summary>
        /// The number of failed probes since the last successful probe.
        /// </summary>
        private int missedProbes;

        public SiloHealthMonitor(
            SiloAddress siloAddress,
            ILoggerFactory loggerFactory,
            IRemoteSiloProber remoteSiloProber)
        {
            this.stopping = this.stoppingCancellation.Token.WhenCancelled();
            this.SiloAddress = siloAddress;
            this.prober = remoteSiloProber;
            this.log = loggerFactory.CreateLogger<SiloHealthMonitor>();
        }

        internal interface ITestAccessor
        {
            int MissedProbes { get; }
        }

        /// <summary>
        /// The silo which this instance is responsible for.
        /// </summary>
        public SiloAddress SiloAddress { get; }

        /// <summary>
        /// Whether or not this monitor is canceled.
        /// </summary>
        public bool IsCanceled => this.stoppingCancellation.IsCancellationRequested;

        int ITestAccessor.MissedProbes => this.missedProbes;

        public void Cancel() => this.stoppingCancellation.Cancel();

        /// <summary>
        /// Probes the remote silo.
        /// </summary>
        /// <param name="diagnosticProbeNumber">The probe number, for diagnostic purposes.</param>
        /// <param name="cancellation">A token to cancel and fail the probe attempt.</param>
        /// <returns>The number of failed probes since the last successful probe.</returns>
        public async Task<int> Probe(int diagnosticProbeNumber, CancellationToken cancellation)
        {
            var id = Interlocked.Increment(ref this.nextProbeId);

            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace("Going to send Ping #{ProbeNumber}/{Id} to probe silo {Silo}", diagnosticProbeNumber, id, this.SiloAddress);
            }

            var probeTask = this.PerformProbe(id, diagnosticProbeNumber, cancellation);
            var resultTask = await Task.WhenAny(this.stopping, probeTask);

            // If the probe finished and the result was valid then return the number of missed probes.
            if (ReferenceEquals(resultTask, probeTask) && probeTask.GetAwaiter().GetResult()) return this.missedProbes;

            // The probe was superseded or the monitor is being shutdown.
            return -1;
        }

        private async Task<bool> PerformProbe(long id, int diagnosticProbeNumber, CancellationToken cancellation)
        {
            try
            {
                var probeCancellation = cancellation.WhenCancelled();
                var task = await Task.WhenAny(probeCancellation, this.prober.Probe(this.SiloAddress, diagnosticProbeNumber));

                if (ReferenceEquals(task, probeCancellation))
                {
                    return this.RecordFailure(id, diagnosticProbeNumber, new OperationCanceledException($"The ping attempt was cancelled. Ping #{diagnosticProbeNumber}/{id}"));
                }
                else
                {
                    await task;
                    return this.RecordSuccess(id, diagnosticProbeNumber);
                }
            }
            catch (Exception exception)
            {
                return this.RecordFailure(id, diagnosticProbeNumber, exception);
            }
        }

        private bool RecordSuccess(long id, int diagnosticProbeNumber)
        {
            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace("Got successful ping response for ping #{ProbeNumber}/{Id} from {Silo}", diagnosticProbeNumber, id, this.SiloAddress);
            }

            MessagingStatisticsGroup.OnPingReplyReceived(this.SiloAddress);

            lock (this.lockObj)
            {
                if (id <= this.highestCompletedProbeId)
                {
                    this.log.Info(
                        "Ignoring success result for ping #{ProbeNumber}/{Id} from {Silo} since a later probe has already completed. Highest ({HighestCompletedProbeId}) > Current ({CurrentProbeId})",
                        diagnosticProbeNumber,
                        id,
                        this.SiloAddress,
                        this.highestCompletedProbeId,
                        id);
                    return false;
                }
                else if (this.stoppingCancellation.IsCancellationRequested)
                {
                    this.log.Info(
                        "Ignoring success result for ping #{ProbeNumber}/{Id} from {Silo} since this monitor has been stopped",
                        diagnosticProbeNumber,
                        id,
                        this.SiloAddress);
                    return false;
                }
                else
                {
                    this.highestCompletedProbeId = id;
                    Interlocked.Exchange(ref this.missedProbes, 0);
                    return true;
                }
            }
        }

        private bool RecordFailure(long id, int diagnosticProbeNumber, Exception failureReason)
        {
            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace("Got failed ping response for ping #{ProbeNumber}/{Id} from {Silo}: {Exception}", diagnosticProbeNumber, id, this.SiloAddress, failureReason);
            }

            MessagingStatisticsGroup.OnPingReplyMissed(this.SiloAddress);

            lock (this.lockObj)
            {
                if (id <= this.highestCompletedProbeId)
                {
                    this.log.Info(
                        "Ignoring failure result for ping #{ProbeNumber}/{Id} from {Silo} since a later probe has already completed. Highest completed id ({HighestCompletedProbeId})",
                        diagnosticProbeNumber,
                        id,
                        this.SiloAddress,
                        this.highestCompletedProbeId,
                        id);
                    return false;
                }
                else if (this.stoppingCancellation.IsCancellationRequested)
                {
                    this.log.Info(
                        "Ignoring failure result for ping #{ProbeNumber}/{Id} from {Silo} since this monitor has been stopped",
                        diagnosticProbeNumber,
                        id,
                        this.SiloAddress);
                    return false;
                }
                else
                {
                    this.highestCompletedProbeId = id;
                    var missed = Interlocked.Increment(ref this.missedProbes);

                    this.log.LogWarning(
                        (int)ErrorCode.MembershipMissedPing,
                        "Did not get ping response for ping #{ProbeNumber}/{Id} from {Silo}: {Exception}",
                        diagnosticProbeNumber,
                        id,
                        this.SiloAddress,
                        failureReason);
                    if (this.log.IsEnabled(LogLevel.Trace))
                    {
                        this.log.LogTrace("Current number of failed probes for {Silo}: {MissedProbes}", this.SiloAddress, missed);
                    }

                    return true;
                }
            }
        }
    }
}
