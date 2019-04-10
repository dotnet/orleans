using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for monitoring an individual remote silo.
    /// </summary>
    internal class SiloHealthMonitor
    {
        private readonly ILogger log;
        private readonly IInternalGrainFactory grainFactory;

        /// <summary>
        /// The number of failed probes since the last successful probe.
        /// </summary>
        private int missedProbes;

        public SiloHealthMonitor(
            SiloAddress siloAddress,
            ILoggerFactory loggerFactory,
            IInternalGrainFactory grainFactory)
        {
            this.SiloAddress = siloAddress;
            this.grainFactory = grainFactory;
            this.log = loggerFactory.CreateLogger($"{nameof(SiloHealthMonitor)}/{this.SiloAddress}");
        }

        /// <summary>
        /// The silo which this instance is responsible for.
        /// </summary>
        public SiloAddress SiloAddress { get; }

        /// <summary>
        /// Probes the remote silo.
        /// </summary>
        /// <param name="probeNumber">The probe number, used for diagnostic purposes.</param>
        /// <returns>The number of failed probes since the last successful probe.</returns>
        public async Task<int> Probe(int probeNumber)
        {
            try
            {
                await this.SendPing(probeNumber);
                return this.RecordSuccess(probeNumber);
            }
            catch (Exception exception)
            {
                return this.RecordFailure(probeNumber, exception);
            }
        }

        private Task SendPing(int probeNumber)
        {
            if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Going to send Ping #{ProbeNumber} to probe silo {Silo}", probeNumber, this.SiloAddress);
            Task pingTask;
            try
            {
                RequestContext.Set(RequestContext.PING_APPLICATION_HEADER, true);
                var remoteSilo = this.grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipOracleId, this.SiloAddress);
                pingTask = remoteSilo.Ping(probeNumber);

                // Update stats counters -- only count Pings that were successfuly sent [but not necessarily replied to]
                MessagingStatisticsGroup.OnPingSend(this.SiloAddress);
            }
            finally
            {
                RequestContext.Remove(RequestContext.PING_APPLICATION_HEADER);
            }

            return pingTask;
        }

        private int RecordSuccess(int probeNumber)
        {
            if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Got successful ping response for ping #{ProbeNumber} from {Silo}", probeNumber, this.SiloAddress);
            MessagingStatisticsGroup.OnPingReplyReceived(this.SiloAddress);
            Interlocked.Exchange(ref this.missedProbes, 0);
            return 0;
        }

        private int RecordFailure(int probeNumber, Exception failureReason)
        {
            MessagingStatisticsGroup.OnPingReplyMissed(this.SiloAddress);
            var missedProbes = Interlocked.Increment(ref this.missedProbes);
            this.log.LogWarning((int)ErrorCode.MembershipMissedPing, "Did not get ping response for ping #{ProbeNumber} from {Silo}: {Exception}", probeNumber, this.SiloAddress, failureReason);
            if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Current number of failed probes for {Silo}: {MissedProbes}", this.SiloAddress, missedProbes);
            return missedProbes;
        }
    }
}
