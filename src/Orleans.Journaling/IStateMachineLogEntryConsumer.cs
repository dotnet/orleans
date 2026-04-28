using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Consumes decoded state machine log entries.
/// </summary>
public interface IStateMachineLogEntryConsumer
{
    /// <summary>
    /// Applies one decoded log entry.
    /// </summary>
    /// <param name="streamId">The durable state machine id.</param>
    /// <param name="payload">The encoded durable operation payload.</param>
    void OnEntry(StateMachineId streamId, ReadOnlySequence<byte> payload);
}
