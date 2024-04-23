using static System.String;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Represents an acknowledgement from storage that a message was enqueued.
/// This is used to surface any generated identifiers for testing.
/// </summary>
internal record AdoNetStreamMessageAck(
    string ServiceId,
    string ProviderId,
    int QueueId,
    long MessageId)
{
    public AdoNetStreamMessageAck() : this(Empty, Empty, 0, 0)
    {
    }
}