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
    /// Gets the latest time at which the sender can deliver this message again, on the sender's Durable Jobs clock, or
    /// <see langword="null"/> when nothing redelivers it.
    /// </summary>
    /// <remarks>
    /// The outbox stamps each delivery attempt with the time its retries end plus the attempt's longest duration. An
    /// inbox which first accepts the message keeps its deduplication record for <c>DeduplicationWindow</c>, so it answers
    /// <see cref="DeliveryResult.DeduplicationWindowExceededCode"/> for a deadline beyond that window rather than accept a
    /// message whose redelivery it couldn't recognize. The comparison assumes clock skew between silos is much smaller
    /// than the window.
    /// </remarks>
    [Id(1)]
    public DateTimeOffset? RedeliveryDeadline { get; init; }}
