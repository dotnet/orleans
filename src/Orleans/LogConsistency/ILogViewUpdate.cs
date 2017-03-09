using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.LogConsistency
{
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