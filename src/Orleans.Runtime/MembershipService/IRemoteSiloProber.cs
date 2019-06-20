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
    }
}
