namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a message that was successfully confirmed.
/// </summary>
internal record AdoNetStreamConfirmationAck(
    string ServiceId,
    string ProviderId,
    string QueueId,
    long MessageId)
{
    public AdoNetStreamConfirmationAck() : this("", "", "", 0)
    {
    }
}