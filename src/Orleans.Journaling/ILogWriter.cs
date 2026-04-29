namespace Orleans.Journaling;

/// <summary>
/// Provides functionality for writing out-of-band log entries to the log for the state machine which holds this instance.
/// </summary>
public interface ILogWriter
{
    /// <summary>
    /// Begins writing an entry to the log for the state machine which holds this instance.
    /// </summary>
    /// <returns>A lexical scope for the pending log entry. Dispose the returned value to abort the entry if <see cref="LogEntry.Commit"/> is not called.</returns>
    LogEntry BeginEntry();

}
