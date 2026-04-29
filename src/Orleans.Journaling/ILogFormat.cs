using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Reads and writes the physical byte format used to persist state machine log entries.
/// </summary>
/// <remarks>
/// A log format owns physical framing for entries. Durable state machine codecs write only
/// the payload for a single entry.
/// </remarks>
public interface ILogFormat
{
    /// <summary>
    /// Creates a writer for a new mutable log segment.
    /// </summary>
    /// <returns>A new log segment writer.</returns>
    ILogSegmentWriter CreateWriter();

    /// <summary>
    /// Reads complete log entries from the beginning of <paramref name="input"/> and pushes decoded entries to <paramref name="sink"/>.
    /// </summary>
    /// <param name="input">The buffered persisted log data. The caller retains ownership and disposes it after this call returns.</param>
    /// <param name="sink">The sink which receives decoded log entries.</param>
    /// <param name="isCompleted">A value indicating whether no more persisted bytes will be supplied after <paramref name="input"/>.</param>
    /// <returns>The read result indicating how many bytes were consumed and, if incomplete data remains, when to retry.</returns>
    LogFormatReadResult Read(ArcBuffer input, ILogEntrySink sink, bool isCompleted);
}

/// <summary>
/// The result of reading log entries from buffered persisted data.
/// </summary>
public readonly struct LogFormatReadResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LogFormatReadResult"/> struct.
    /// </summary>
    /// <param name="bytesConsumed">The number of bytes consumed from the beginning of the input buffer.</param>
    /// <param name="minimumBufferLength">The minimum length of the retained buffer before retrying, if known.</param>
    public LogFormatReadResult(int bytesConsumed, int? minimumBufferLength = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesConsumed);
        if (minimumBufferLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumBufferLength));
        }

        BytesConsumed = bytesConsumed;
        MinimumBufferLength = minimumBufferLength;
    }

    /// <summary>
    /// Gets the number of bytes consumed from the beginning of the input buffer.
    /// </summary>
    public int BytesConsumed { get; }

    /// <summary>
    /// Gets the minimum length of the retained buffer before retrying, if known.
    /// </summary>
    public int? MinimumBufferLength { get; }
}
