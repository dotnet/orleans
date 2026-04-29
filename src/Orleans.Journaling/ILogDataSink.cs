using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Consumes raw persisted log data during recovery.
/// </summary>
public interface ILogDataSink
{
    /// <summary>
    /// Receives one ordered chunk of persisted log data.
    /// </summary>
    /// <param name="data">
    /// The persisted log data chunk. The caller retains ownership and disposes it after this call returns.
    /// Consumers must not retain the buffer beyond the call unless they explicitly copy or pin it.
    /// Chunk boundaries are not log-entry boundaries.
    /// </param>
    void OnLogData(ArcBuffer data);
}
