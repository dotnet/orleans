using System;

namespace Orleans.Durable.Messaging;

/// <summary>A received command whose destination is the grain owning the inbox.</summary>
[GenerateSerializer, Alias("Orleans.Durable.Messaging.InboxMessage")]
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
    /// The receiving inbox keeps each pending message until it is processed, including a message whose
    /// <see cref="Envelope.DeliverAt"/> is later. It retains each processed record for <c>DeduplicationWindow</c> after
    /// the send time it accepted. A delivery of a tracked message receives its recorded outcome, whatever its send time.
    /// Before removing records, the inbox journals an admission floor, which only advances. It answers a message it
    /// doesn't track with <see cref="DeliveryResult.Expired"/>, staging nothing for it, when the message's send time is
    /// at or below that floor.
    /// </para>
    /// <para>
    /// The floor survives recovery and migration, so a delayed retry of a removed message stays expired even after a
    /// clock moves backward. Clock jumps and skew change when messages expire and how long records are retained.
    /// Once a record is removed, sending its command again with a later send time is admitted as a new delivery, so
    /// applications reconcile an uncertain outcome before doing so.
    /// </para>
    /// <para>
    /// The inbox never accepts a delivered send time later than its own clock plus <c>DeduplicationWindow</c>. It rejects
    /// such a delivery of an untracked message above the admission floor with an <see cref="ArgumentOutOfRangeException"/>,
    /// staging nothing, so the sender retries it within its retry budget. A tracked message still receives its recorded
    /// outcome, and an untracked message at or below the floor still receives <see cref="DeliveryResult.Expired"/>.
    /// A message a grain sends to itself uses its clock, or the first time above the admission floor after a backward
    /// clock jump. Every processed record therefore becomes eligible for removal within twice the window of the
    /// receiver's clock, or just after the floor when a self-send follows a backward jump.
    /// </para>
    /// </remarks>
    [Id(1)]
    public required DateTimeOffset SentAt { get; init; }
}
