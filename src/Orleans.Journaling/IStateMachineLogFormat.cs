using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Reads and writes the physical byte format used to persist state machine log entries.
/// </summary>
/// <remarks>
/// A log format owns physical framing for entries. Durable state machine codecs write only
/// the payload for a single entry.
/// </remarks>
public interface IStateMachineLogFormat
{
    /// <summary>
    /// Creates a writer for a new mutable log extent.
    /// </summary>
    /// <returns>A new log extent writer.</returns>
    IStateMachineLogExtentWriter CreateWriter();

    /// <summary>
    /// Reads persisted log data and pushes decoded entries to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="input">The persisted log data. The caller retains ownership and disposes it after this call returns.</param>
    /// <param name="consumer">The consumer which receives decoded log entries.</param>
    void Read(ArcBuffer input, IStateMachineLogEntryConsumer consumer);
}
