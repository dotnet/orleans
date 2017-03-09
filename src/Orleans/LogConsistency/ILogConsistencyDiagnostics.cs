using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Interface for diagnostics.
    /// </summary>
    public interface ILogConsistencyDiagnostics
    {

        /// <summary>Gets a list of all currently unresolved connection issues.</summary>
        IEnumerable<ConnectionIssue> UnresolvedConnectionIssues { get; }

        /// <summary>Turns on the statistics collection for this log-consistent grain.</summary>
        void EnableStatsCollection();

        /// <summary>Turns off the statistics collection for this log-consistent grain.</summary>
        void DisableStatsCollection();

        /// <summary>Gets the collected statistics for this log-consistent grain.</summary>
        LogConsistencyStatistics GetStats();

    }
}
