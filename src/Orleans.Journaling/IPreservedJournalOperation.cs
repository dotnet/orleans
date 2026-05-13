using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// A format-owned journal operation which can be copied forward without interpreting it.
/// </summary>
public interface IPreservedJournalOperation
{
    /// <summary>
    /// Gets the journal format key for this preserved operation.
    /// </summary>
    string FormatKey { get; }

    /// <summary>
    /// Gets the operation payload bytes for the preserved operation.
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }
}

internal sealed class PreservedJournalOperation : IPreservedJournalOperation
{
    private readonly byte[] _payload;

    public PreservedJournalOperation(string formatKey, ReadOnlySequence<byte> payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public PreservedJournalOperation(string formatKey, JournalReadBuffer payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public PreservedJournalOperation(string formatKey, ReadOnlyMemory<byte> payload)
    {
        FormatKey = JournalFormatServices.ValidateJournalFormatKey(formatKey);
        _payload = payload.ToArray();
    }

    public string FormatKey { get; }

    public ReadOnlyMemory<byte> Payload => _payload;
}
