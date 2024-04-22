using static System.String;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Represents an acknowledgement from storage that a message was enqueued.
/// This is used to surface any generated identifiers for testing.
/// </summary>
internal record AdoNetStreamAck(
    string ServiceId,
    string ProviderId,
    int QueueId,
    long MessageId)
{
    public AdoNetStreamAck() : this(Empty, Empty, 0, 0)
    {
    }
}