namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a message that can be confirmed.
/// </summary>
internal record AdoNetStreamConfirmation(
    long MessageId,
    int Dequeued)
{
    public AdoNetStreamConfirmation() : this(0, 0)
    {
    }
}