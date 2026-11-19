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
    /// Stages removal of an inbox dead letter.
    /// </summary>
    /// <param name="messageId">The receiver-local command identity.</param>
    /// <returns><see langword="true"/> when the dead letter existed and was removed.</returns>
    /// <remarks>
    /// The removal becomes durable with the grain's next journal write.
    /// </remarks>
    bool RemoveInboxDeadLetter(MessageId messageId);
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

internal sealed class MessagingDiagnostics(Inbox inbox) : IMessagingDiagnostics
{
    public IReadOnlyList<DeadLetter> InboxDeadLetters =>
        inbox.DeadLetterState.Values.Select(static entry => new DeadLetter
        {
            Message = entry.Message,
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public bool RemoveInboxDeadLetter(MessageId messageId) => inbox.RemoveDeadLetter(messageId);
}
