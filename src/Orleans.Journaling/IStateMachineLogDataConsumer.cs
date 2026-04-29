using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Consumes raw persisted state machine log data during recovery.
/// </summary>
public interface IStateMachineLogDataConsumer
{
    /// <summary>
    /// Receives one ordered buffer of persisted log data.
    /// </summary>
    /// <param name="data">
    /// The persisted log data. The caller retains ownership and disposes it after this call returns.
    /// Consumers must not retain the buffer beyond the call unless they explicitly copy or pin it.
    /// </param>
    void OnLogData(ArcBuffer data);
}
