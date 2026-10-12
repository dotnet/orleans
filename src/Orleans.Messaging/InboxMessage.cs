namespace Orleans.Messaging;

/// <summary>A received command whose destination is the grain owning the inbox.</summary>
[GenerateSerializer, Alias("Orleans.Messaging.InboxMessage")]
public readonly struct InboxMessage
{
    /// <summary>Gets the command identity, payload, and application metadata.</summary>
    [Id(0)]
    public required Envelope Envelope { get; init; }
}
