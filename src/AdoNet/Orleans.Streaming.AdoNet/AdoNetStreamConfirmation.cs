using static System.String;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a message that was successfully confirmed.
/// </summary>
internal record AdoNetStreamConfirmation(
    string ServiceId,
    string ProviderId,
    int QueueId,
    long MessageId)
{
    public AdoNetStreamConfirmation() : this(Empty, Empty, 0, 0)
    {
    }
}