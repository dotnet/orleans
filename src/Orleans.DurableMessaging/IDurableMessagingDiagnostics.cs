using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

/// <summary>
/// Provides a read-only view of a grain's durable messaging state.
/// </summary>
public interface IDurableMessagingDiagnostics
{
    /// <summary>
    /// Gets messages which failed during inbox processing.
    /// </summary>
    /// <remarks>The caller owns the returned entries and must dispose each one.</remarks>
    IReadOnlyList<DurableDeadLetter> InboxDeadLetters { get; }

    /// <summary>
    /// Gets messages which could not be delivered from the outbox.
    /// </summary>
    /// <remarks>The caller owns the returned entries and must dispose each one.</remarks>
    IReadOnlyList<DurableDeadLetter> OutboxDeadLetters { get; }
}

/// <summary>
/// Describes a dead-lettered durable message.
/// </summary>
public sealed class DurableDeadLetter : IDisposable
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

    /// <inheritdoc />
    public void Dispose() => Message.Dispose();
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
            Message = entry.Envelope.Retain(),
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public IReadOnlyList<DurableDeadLetter> OutboxDeadLetters =>
        outbox.Values.Select(static entry => new DurableDeadLetter
        {
            Message = entry.Envelope.Retain(),
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();
}
