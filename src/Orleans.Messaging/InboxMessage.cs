using System;

namespace Orleans.Messaging;

/// <summary>A received command whose destination is the grain owning the inbox.</summary>
[GenerateSerializer, Alias("Orleans.Messaging.InboxMessage")]
public readonly struct InboxMessage
{
    /// <summary>Gets the command identity, payload, and application metadata.</summary>
    [Id(0)]
    public required Envelope Envelope { get; init; }

    /// <summary>
    /// Gets the time, on the sending grain's Durable Jobs clock, at which this logical send was staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox journals this value when it stages the send, and every delivery attempt and recovery reuses it.
    /// Direct callers of <see cref="IMessagingExtension.DeliverAsync"/> likewise reuse it when retrying a command.
    /// </para>
    /// <para>
    /// The receiving inbox retains each processed record for <c>DeduplicationWindow</c> after its send time. Before
    /// removing records, it journals an admission floor, which only advances. The inbox answers a message it doesn't
    /// track with <see cref="DeliveryStatus.Expired"/>, staging nothing for it, when the message's send time is at or
    /// below that floor. Tracked messages receive their recorded outcome.
    /// </para>
    /// <para>
    /// The floor survives recovery and migration, so a delayed retry of a removed message stays fenced even after a
    /// clock moves backward. Clock jumps and skew change when messages expire and how long records are retained.
    /// Sending a command again with a new send time creates a new admission opportunity, so applications reconcile an
    /// uncertain outcome before doing so.
    /// </para>
    /// </remarks>
    [Id(1)]
    public required DateTimeOffset SentAt { get; init; }
}
