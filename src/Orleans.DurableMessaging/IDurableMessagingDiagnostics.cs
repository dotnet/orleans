using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Provides operational access to a grain's durable messaging state.
/// </summary>
public interface IDurableMessagingDiagnostics
{
    /// <summary>
    /// Gets messages which failed during inbox processing.
    /// </summary>
    IReadOnlyList<DurableDeadLetter> InboxDeadLetters { get; }

    /// <summary>
    /// Gets messages which could not be delivered from the outbox.
    /// </summary>
    IReadOnlyList<DurableDeadLetter> OutboxDeadLetters { get; }

    /// <summary>
    /// Stages removal of an inbox dead letter.
    /// </summary>
    /// <param name="senderId">The original sender grain identifier.</param>
    /// <param name="messageId">The message identifier.</param>
    /// <returns><see langword="true"/> when the dead letter existed and was removed.</returns>
    /// <remarks>
    /// The removal becomes durable with the grain's next journal write.
    /// </remarks>
    bool RemoveInboxDeadLetter(GrainId senderId, Guid messageId);

    /// <summary>
    /// Stages removal of an outbox dead letter.
    /// </summary>
    /// <param name="messageId">The message identifier.</param>
    /// <returns><see langword="true"/> when the dead letter existed and was removed.</returns>
    /// <remarks>
    /// The removal becomes durable with the grain's next journal write.
    /// </remarks>
    bool RemoveOutboxDeadLetter(Guid messageId);
}

/// <summary>
/// Describes a dead-lettered durable message.
/// </summary>
public sealed class DurableDeadLetter
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    public required DurableEnvelope Message { get; init; }

    /// <summary>
    /// Gets when the message was dead-lettered.
    /// </summary>
    public DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>
    /// Gets the terminal failure reason.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the number of attempts made.
    /// </summary>
    public int AttemptCount { get; init; }
}

internal sealed class DurableMessagingDiagnostics(
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices("inbox-dead-letters")]
    IDurableDictionary<(Orleans.Runtime.GrainId, Guid), InboxDeadLetter> inbox,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices("outbox-dead-letters")]
    IDurableDictionary<Guid, OutboxDeadLetter> outbox) : IDurableMessagingDiagnostics
{
    public IReadOnlyList<DurableDeadLetter> InboxDeadLetters =>
        inbox.Values.Select(static entry => new DurableDeadLetter
        {
            Message = entry.Envelope,
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public IReadOnlyList<DurableDeadLetter> OutboxDeadLetters =>
        outbox.Values.Select(static entry => new DurableDeadLetter
        {
            Message = entry.Envelope,
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public bool RemoveInboxDeadLetter(GrainId senderId, Guid messageId) =>
        inbox.Remove((senderId, messageId));

    public bool RemoveOutboxDeadLetter(Guid messageId) => outbox.Remove(messageId);
}
