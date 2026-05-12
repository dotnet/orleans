using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// A format-owned journal entry which can be copied forward without interpreting it.
/// </summary>
public interface IFormattedJournalEntry
{
    /// <summary>
    /// Gets the journal format key for this formatted entry.
    /// </summary>
    string FormatKey { get; }

    /// <summary>
    /// Gets the operation payload bytes for the formatted entry.
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }
}

internal sealed class FormattedJournalEntry : IFormattedJournalEntry
{
    private readonly byte[] _payload;

    public FormattedJournalEntry(string formatKey, ReadOnlySequence<byte> payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public FormattedJournalEntry(string formatKey, JournalReadBuffer payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public FormattedJournalEntry(string formatKey, ReadOnlyMemory<byte> payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public string FormatKey { get; }

    public ReadOnlyMemory<byte> Payload => _payload;
}
