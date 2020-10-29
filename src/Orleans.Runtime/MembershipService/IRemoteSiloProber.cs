using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for probing remote silos for responsiveness.
    /// </summary>
    internal interface IRemoteSiloProber
    {
        /// <summary>
        /// Probes the specified <paramref name="silo"/> for responsiveness.
        /// </summary>
        /// <param name="silo">The silo to probe.</param>
        /// <param name="probeNumber">The probe identifier for diagnostic purposes.</param>
        /// <returns>
        /// A <see cref="Task"/> which completes when the probe returns successfully and faults when the probe fails.
        /// </returns>
        Task Probe(SiloAddress silo, int probeNumber);

        /// <summary>
        /// Probes the specified <paramref name="target"/> indirectly, via <paramref name="intermediary"/>.
        /// </summary>
        /// <param name="intermediary">The silo which will perform a direct probe.</param>
        /// <param name="target">The silo which will be probed.</param>
        /// <param name="probeTimeout">The timeout which the <paramref name="intermediary" /> should apply to the probe.</param>
        /// <param name="probeNumber">The probe number for diagnostic purposes.</param>
        Task<IndirectProbeResponse> ProbeIndirectly(SiloAddress intermediary, SiloAddress target, TimeSpan probeTimeout, int probeNumber);
    }
}
