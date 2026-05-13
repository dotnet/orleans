using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// A format-owned journal entry which can be copied forward without interpreting it.
/// </summary>
public interface IPreservedJournalEntry
{
    /// <summary>
    /// Gets the journal format key for this preserved entry.
    /// </summary>
    string FormatKey { get; }

    /// <summary>
    /// Gets the entry payload bytes for the preserved entry.
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }
}

internal sealed class PreservedJournalEntry : IPreservedJournalEntry
{
    private readonly byte[] _payload;

    public PreservedJournalEntry(string formatKey, ReadOnlySequence<byte> payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public PreservedJournalEntry(string formatKey, JournalBufferReader payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public PreservedJournalEntry(string formatKey, ReadOnlyMemory<byte> payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public string FormatKey { get; }

    public ReadOnlyMemory<byte> Payload => _payload;
}
