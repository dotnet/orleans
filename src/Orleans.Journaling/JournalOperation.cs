using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Represents one persisted journal operation payload.
/// </summary>
/// <remarks>
/// <para>
/// The payload is owned by the journal format identified by <see cref="FormatKey"/>. This type does
/// not interpret the payload and has no knowledge of any specific physical format.
/// </para>
/// <para>
/// The payload is only guaranteed to remain valid for the duration of the synchronous replay call
/// which supplies this operation. Implementations which need to retain the payload must copy it.
/// </para>
/// </remarks>
/// <param name="formatKey">The journal format key for <paramref name="payload"/>.</param>
/// <param name="payload">The operation payload bytes.</param>
public readonly struct JournalOperation(string formatKey, ReadOnlySequence<byte> payload)
{
    /// <summary>
    /// Gets the journal format key for this operation.
    /// </summary>
    public string FormatKey { get; } = JournalFormatServices.ValidateJournalFormatKey(formatKey);

    /// <summary>
    /// Gets the operation payload bytes.
    /// </summary>
    public ReadOnlySequence<byte> Payload { get; } = payload;
}
