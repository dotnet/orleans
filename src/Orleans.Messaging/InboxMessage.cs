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
    /// The receiving inbox retains each pending and processed record for <c>DeduplicationWindow</c> after the latest
    /// send time it has observed for that message ID. A delivery of a tracked message receives its recorded outcome;
    /// when its send time is later than the recorded one, the inbox durably extends the record to that time before
    /// answering, and an earlier or equal send time leaves the record unchanged. Before removing records, the inbox
    /// journals an admission floor, which only advances. It answers a message it doesn't track with
    /// <see cref="DeliveryStatus.Expired"/>, staging nothing for it, when the message's send time is at or below that
    /// floor.
    /// </para>
    /// <para>
    /// The floor survives recovery and migration, so a delayed retry of a removed message stays expired even after a
    /// clock moves backward. Clock jumps and skew change when messages expire and how long records are retained.
    /// Once a record is removed, sending its command again with a later send time is admitted as a new delivery, so
    /// applications reconcile an uncertain outcome before doing so.
    /// </para>
    /// <para>
    /// The inbox never stores a send time later than its own clock plus <c>DeduplicationWindow</c>. It rejects such a
    /// delivery with an <see cref="ArgumentOutOfRangeException"/>, staging nothing, when the message is untracked and
    /// above the admission floor or the send time would extend its record, so the sender retries it within its retry
    /// budget. A tracked message whose send time is equal to or earlier than its record's still receives its recorded
    /// outcome, and an untracked message at or below the floor still receives <see cref="DeliveryStatus.Expired"/>.
    /// Every record therefore becomes eligible for removal within twice the window of the receiver's clock.
    /// </para>
    /// </remarks>
    [Id(1)]
    public required DateTimeOffset SentAt { get; init; }
}
