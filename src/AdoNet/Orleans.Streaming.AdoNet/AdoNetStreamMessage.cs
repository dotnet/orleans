using static System.String;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a stored message in an ADONET streaming provider.
/// </summary>
internal record AdoNetStreamMessage(
    string ServiceId,
    string ProviderId,
    int QueueId,
    long MessageId,
    int Dequeued,
    DateTime VisibleOn,
    DateTime ExpiresOn,
    DateTime CreatedOn,
    DateTime ModifiedOn,
    byte[] Payload)
{
    public AdoNetStreamMessage() : this(Empty, Empty, 0, 0, 0, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, [])
    {
    }
}