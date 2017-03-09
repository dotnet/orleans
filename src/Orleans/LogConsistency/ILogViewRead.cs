using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Interface for reading the log view.
    /// </summary>
    /// <typeparam name="TView">The type of the view (state of the grain).</typeparam>
    /// <typeparam name="TLogEntry">The type of log entries.</typeparam>
    public interface ILogViewRead<TView, TLogEntry>
    {
        /// <summary>
        /// Local, tentative view of the log (reflecting both confirmed and unconfirmed entries)
        /// </summary>
        TView TentativeView { get; }

        /// <summary>
        /// Confirmed view of the log (reflecting only confirmed entries)
        /// </summary>
        TView ConfirmedView { get; }

        /// <summary>
        /// The length of the confirmed prefix of the log
        /// </summary>
        int ConfirmedVersion { get; }

        /// <summary>
        /// A list of the submitted entries that do not yet appear in the confirmed prefix.
        /// </summary>
        IEnumerable<TLogEntry> UnconfirmedSuffix { get; }

        /// <summary>
        /// Attempt to retrieve a segment of the log, possibly from storage. Throws <see cref="NotSupportedException"/> if
        /// the log cannot be read, which depends on the providers used and how they are configured.
        /// </summary>
        /// <param name="fromVersion">the start position </param>
        /// <param name="toVersion">the end position</param>
        /// <returns>a </returns>
        Task<IReadOnlyList<TLogEntry>> RetrieveLogSegment(int fromVersion, int toVersion);

    }
}