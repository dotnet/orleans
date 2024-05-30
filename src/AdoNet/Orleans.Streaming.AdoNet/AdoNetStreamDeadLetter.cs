namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a dead letter in an ADONET streaming provider.
/// </summary>
internal record AdoNetStreamDeadLetter(
    string ServiceId,
    string ProviderId,
    string QueueId,
    long MessageId,
    int Dequeued,
    DateTime VisibleOn,
    DateTime ExpiresOn,
    DateTime CreatedOn,
    DateTime ModifiedOn,
    DateTime DeadOn,
    DateTime RemoveOn,
    byte[] Payload)
{
    public AdoNetStreamDeadLetter() : this("", "", "", 0, 0, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, [])
    {
    }
}