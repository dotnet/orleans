using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>An outgoing command with destination routing separate from its common envelope.</summary>
[GenerateSerializer, Alias("Orleans.Messaging.OutboxMessage")]
public readonly struct OutboxMessage
{
    /// <summary>Gets the command identity, payload, and application metadata.</summary>
    [Id(0)]
    public required Envelope Envelope { get; init; }

    /// <summary>Gets the required destination grain identity.</summary>
    [Id(1)]
    public required GrainId ReceiverId { get; init; }

    /// <summary>Creates the incoming view by sharing the envelope, payload, and headers.</summary>
    /// <returns>A received message whose destination is implied by the receiving grain.</returns>
    public InboxMessage ToInboxMessage() => new() { Envelope = Envelope };
}
