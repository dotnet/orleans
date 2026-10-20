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
    /// Gets the immutable admission cutoff for this logical send, or <see langword="null"/> for permanent
    /// deduplication tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox journals this value when it stages the send and preserves it across every attempt and recovery.
    /// Direct senders likewise preserve it when retrying the same command. The receiver first checks its pending and
    /// processed records. An untracked message whose cutoff is at or below the receiver's durable admission floor
    /// receives <see cref="DeliveryStatus.Expired"/> before any message effects are staged.
    /// </para>
    /// <para>
    /// The receiver retains a processed record until its admission floor fences the cutoff. The floor only advances
    /// and survives recovery, so forgetting a record permanently fences that send's delayed retries. Clock readings
    /// guide floor advancement and retry scheduling; journaled admission decisions establish deduplication safety.
    /// Forward clock jumps can fence undelivered messages early, and backward jumps can prolong retention.
    /// </para>
    /// <para>
    /// A null cutoff retains the processed record permanently. Reusing a command's identity with a fresh cutoff
    /// after an uncertain delivery creates a new admission opportunity; applications preserve the original cutoff
    /// or reconcile the outcome through their own idempotent protocol.
    /// </para>
    /// </remarks>
    [Id(1)]
    public DateTimeOffset? RedeliveryDeadline { get; init; }
}
