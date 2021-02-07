using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// A log view adaptor is the storage interface for <see cref="LogConsistentGrain{T}"/>, whose state is defined as a log view. 
    ///<para>
    /// There is one adaptor per grain, which is installed by <see cref="ILogViewAdaptorFactory"/> when the grain is activated.
    ///</para>
    /// </summary>
    /// <typeparam name="TLogView"> Type for the log view </typeparam>
    /// <typeparam name="TLogEntry"> Type for the log entry </typeparam>
    public interface ILogViewAdaptor<TLogView, TLogEntry> :
          ILogViewRead<TLogView, TLogEntry>,
          ILogViewUpdate<TLogEntry>,
          ILogConsistencyDiagnostics
        where TLogView : new()
    {
        /// <summary>Called during activation, right before the user-defined <see cref="Grain.OnActivateAsync"/>.</summary>
        Task PreOnActivate();

        /// <summary>Called during activation, right after the user-defined <see cref="Grain.OnActivateAsync"/>..</summary>
        Task PostOnActivate();

        /// <summary>Called during deactivation, right after the user-defined <see cref="Grain.OnDeactivateAsync"/>.</summary>
        Task PostOnDeactivate();
    }

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

    /// <summary>
    /// Interface for updating the log.
    /// </summary>
    /// <typeparam name="TLogEntry">The type of log entries.</typeparam>
    public interface ILogViewUpdate<TLogEntry>
    {
        /// <summary>
        /// Submit a single log entry to be appended to the global log,
        /// either at the current or at any later position.
        /// </summary>
        void Submit(TLogEntry entry);

        /// <summary>
        /// Submit a range of log entries to be appended atomically to the global log,
        /// either at the current or at any later position.
        /// </summary>
        void SubmitRange(IEnumerable<TLogEntry> entries);

        /// <summary>
        /// Try to append a single log entry at the current position of the log.
        /// </summary>
        /// <returns>true if the entry was appended successfully, or false 
        /// if there was a concurrency conflict (i.e. some other entries were previously appended).
        /// </returns>
        Task<bool> TryAppend(TLogEntry entry);

        /// <summary>
        /// Try to append a range of log entries atomically at the current position of the log.
        /// </summary>
        /// <returns>true if the entries were appended successfully, or false 
        /// if there was a concurrency conflict (i.e. some other entries were previously appended).
        /// </returns>
        Task<bool> TryAppendRange(IEnumerable<TLogEntry> entries);

        /// <summary>
        /// Confirm all submitted entries.
        ///<para>Waits until all previously submitted entries appear in the confirmed prefix of the log.</para>
        /// </summary>
        /// <returns>A task that completes after all entries are confirmed.</returns>
        Task ConfirmSubmittedEntries();

        /// <summary>
        /// Get the latest log view and confirm all submitted entries.
        ///<para>Waits until all previously submitted entries appear in the confirmed prefix of the log, and forces a refresh of the confirmed prefix.</para>
        /// </summary>
        /// <returns>A task that completes after getting the latest version and confirming all entries.</returns>
        Task Synchronize();
    }
}
