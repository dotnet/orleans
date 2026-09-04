namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a stored message in an ADONET streaming provider.
/// </summary>
internal record AdoNetStreamMessage(
    string ServiceId,
    string ProviderId,
    string QueueId,
    long MessageId,
    byte[] StreamIdBytes,
    int StreamNamespaceLength,
    DateTime CreatedOn,
    byte[] Payload)
{
    public AdoNetStreamMessage() : this("", "", "", 0, [], 0, DateTime.MinValue, [])
    {
    }

    /// <summary>
    /// Gets the canonical stream identifier stored with this message.
    /// </summary>
    public StreamId StreamId => StreamId.Create(
        StreamIdBytes.AsSpan(0, StreamNamespaceLength),
        StreamIdBytes.AsSpan(StreamNamespaceLength));
}
