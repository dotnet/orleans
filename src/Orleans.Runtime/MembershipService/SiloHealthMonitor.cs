using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
            this.log = loggerFactory.CreateLogger($"{nameof(SiloHealthMonitor)}/{this.SiloAddress}");
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
            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace("Going to send Ping #{ProbeNumber} to probe silo {Silo}", diagnosticProbeNumber, this.SiloAddress);
            }

            var id = Interlocked.Increment(ref this.nextProbeId);
            var probeTask = this.PerformProbe(id, diagnosticProbeNumber, cancellation);
            await Task.WhenAny(this.stopping, probeTask);
            
            return this.missedProbes;
        }

        private async Task PerformProbe(long id, int diagnosticProbeNumber, CancellationToken cancellation)
        {
            try
            {
                var probeCancellation = cancellation.WhenCancelled();
                var task = await Task.WhenAny(probeCancellation, this.prober.Probe(this.SiloAddress, diagnosticProbeNumber));

                if (ReferenceEquals(task, probeCancellation))
                {
                    this.RecordFailure(id, diagnosticProbeNumber, new OperationCanceledException("The ping attempt was cancelled"));
                }
                else
                {
                    await task;
                    this.RecordSuccess(id, diagnosticProbeNumber);
                }
            }
            catch (Exception exception)
            {
                this.RecordFailure(id, diagnosticProbeNumber, exception);
            }
        }

        private void RecordSuccess(long id, int diagnosticProbeNumber)
        {
            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace("Got successful ping response for ping #{ProbeNumber} from {Silo}", diagnosticProbeNumber, this.SiloAddress);
            }

            MessagingStatisticsGroup.OnPingReplyReceived(this.SiloAddress);

            lock (this.lockObj)
            {
                if (id <= this.highestCompletedProbeId)
                {
                    this.log.Info("Ignoring success result for ping #{ProbeNumber} from {Silo} since a later probe has already completed", diagnosticProbeNumber, this.SiloAddress);
                }
                else if (this.stoppingCancellation.IsCancellationRequested)
                {
                    this.log.Info("Ignoring success result for ping #{ProbeNumber} from {Silo} since this monitor has been stopped", diagnosticProbeNumber, this.SiloAddress);
                }
                else
                {
                    this.highestCompletedProbeId = id;
                    Interlocked.Exchange(ref this.missedProbes, 0);
                }
            }
        }

        private void RecordFailure(long id, int diagnosticProbeNumber, Exception failureReason)
        {
            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace("Got failed ping response for ping #{ProbeNumber} from {Silo}: {Exception}", diagnosticProbeNumber, this.SiloAddress, failureReason);
            }

            MessagingStatisticsGroup.OnPingReplyMissed(this.SiloAddress);

            lock (this.lockObj)
            {
                if (id <= this.highestCompletedProbeId)
                {
                    this.log.Info("Ignoring failure result for ping #{ProbeNumber} from {Silo} since a later probe has already completed", diagnosticProbeNumber, this.SiloAddress);
                }
                else if (this.stoppingCancellation.IsCancellationRequested)
                {
                    this.log.Info("Ignoring failure result for ping #{ProbeNumber} from {Silo} since this monitor has been stopped", diagnosticProbeNumber, this.SiloAddress);
                }
                else
                {
                    this.highestCompletedProbeId = id;
                    var missed = Interlocked.Increment(ref this.missedProbes);

                    this.log.LogWarning(
                        (int)ErrorCode.MembershipMissedPing,
                        "Did not get ping response for ping #{ProbeNumber} from {Silo}: {Exception}",
                        diagnosticProbeNumber,
                        this.SiloAddress,
                        failureReason);
                    if (this.log.IsEnabled(LogLevel.Trace))
                    {
                        this.log.LogTrace("Current number of failed probes for {Silo}: {MissedProbes}", this.SiloAddress, missed);
                    }
                }
            }
        }
    }
}
