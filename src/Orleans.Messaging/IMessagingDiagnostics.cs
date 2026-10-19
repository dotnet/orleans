using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Provides operational access to a grain's messaging state.
/// </summary>
public interface IMessagingDiagnostics
{
    /// <summary>
    /// Gets messages which failed during inbox processing.
    /// </summary>
    IReadOnlyList<DeadLetter> InboxDeadLetters { get; }

    /// <summary>
    /// Gets messages which could not be delivered from the outbox.
    /// </summary>
    /// <remarks>
    /// A message whose <see cref="IOutboxMessageSource"/> failed to provide it has no materialized message: its record
    /// carries only the <see cref="Envelope.MessageId"/>, with an empty payload and a default
    /// <see cref="OutboxMessage.ReceiverId"/>, and its reason records the source's failure.
    /// </remarks>
    IReadOnlyList<OutboxDeadLetter> OutboxDeadLetters { get; }

    /// <summary>
    /// Stages removal of an inbox dead letter.
    /// </summary>
    /// <param name="messageId">The receiver-local command identity.</param>
    /// <returns><see langword="true"/> when the dead letter existed and was removed.</returns>
    /// <remarks>
    /// The removal becomes durable with the grain's next journal write.
    /// </remarks>
    bool RemoveInboxDeadLetter(MessageId messageId);

    /// <summary>
    /// Stages removal of an outbox dead letter.
    /// </summary>
    /// <param name="messageId">The message identifier.</param>
    /// <returns><see langword="true"/> when the dead letter existed and was removed.</returns>
    /// <remarks>
    /// The removal becomes durable with the grain's next journal write.
    /// </remarks>
    bool RemoveOutboxDeadLetter(MessageId messageId);
}

/// <summary>
/// Describes a message which the inbox dead-lettered.
/// </summary>
[GenerateSerializer, Alias("Orleans.Messaging.DeadLetter")]
public sealed class DeadLetter
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    /// <remarks>
    /// The message shares the inbox's retained payload and header buffers, so treat them as immutable, as for every
    /// published <see cref="Envelope"/>. Application diagnostic DTOs use ordinary RPC copying when returning message
    /// buffers.
    /// </remarks>
    [Id(0)]
    public required InboxMessage Message { get; init; }

    /// <summary>
    /// Gets when the message was dead-lettered.
    /// </summary>
    [Id(1)]
    public DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>
    /// Gets the terminal failure reason.
    /// </summary>
    [Id(2)]
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the number of attempts made.
    /// </summary>
    [Id(3)]
    public int AttemptCount { get; init; }
}

/// <summary>
/// Describes a message which the outbox dead-lettered.
/// </summary>
[GenerateSerializer, Alias("Orleans.Messaging.OutboxDeadLetter")]
public sealed class OutboxDeadLetter
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    /// <remarks>
    /// Treat the message's encoded headers as immutable. Application diagnostic DTOs use
    /// ordinary RPC copying when returning message buffers.
    /// </remarks>
    [Id(0)]
    public required OutboxMessage Message { get; init; }

    /// <summary>
    /// Gets when the message was dead-lettered.
    /// </summary>
    [Id(1)]
    public DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>
    /// Gets the terminal failure reason.
    /// </summary>
    [Id(2)]
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the number of attempts made.
    /// </summary>
    [Id(3)]
    public int AttemptCount { get; init; }
}

internal sealed class MessagingDiagnostics(Inbox inbox, Outbox outbox) : IMessagingDiagnostics
{
    public IReadOnlyList<DeadLetter> InboxDeadLetters =>
        inbox.DeadLetterState.Values.Select(static entry => new DeadLetter
        {
            Message = entry.Message,
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public IReadOnlyList<OutboxDeadLetter> OutboxDeadLetters =>
        outbox.DeadLetterState.Values.Select(static entry => new OutboxDeadLetter
        {
            Message = entry.Message,
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public bool RemoveInboxDeadLetter(MessageId messageId) => inbox.RemoveDeadLetter(messageId);

    public bool RemoveOutboxDeadLetter(MessageId messageId) => outbox.RemoveDeadLetter(messageId);
}
