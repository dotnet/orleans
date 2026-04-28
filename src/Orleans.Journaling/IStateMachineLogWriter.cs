namespace Orleans.Journaling;

/// <summary>
/// Provides functionality for writing out-of-band log entries to the log for the state machine which holds this instance.
/// </summary>
public interface IStateMachineLogWriter
{
    /// <summary>
    /// Begins writing an entry to the log for the state machine which holds this instance.
    /// </summary>
    /// <returns>A writer for the pending log entry. The caller must commit or abort it before returning.</returns>
    LogEntryWriter BeginEntry();

}
