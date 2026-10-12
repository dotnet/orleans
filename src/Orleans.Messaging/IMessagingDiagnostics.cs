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
    IReadOnlyList<DeadLetter<InboxMessage>> InboxDeadLetters { get; }

    /// <summary>
    /// Stages removal of an inbox dead letter.
    /// </summary>
    /// <param name="messageId">The receiver-local command identity.</param>
    /// <returns><see langword="true"/> when the dead letter existed and was removed.</returns>
    /// <remarks>
    /// The removal becomes durable with the grain's next journal write.
    /// </remarks>
    bool RemoveInboxDeadLetter(HierarchicalKey messageId);
}

/// <summary>
/// Describes a dead-lettered durable message.
/// </summary>
/// <typeparam name="TMessage">The incoming or outgoing message type.</typeparam>
public sealed class DeadLetter<TMessage>
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    /// <remarks>
    /// Treat the message's encoded headers as immutable. Application diagnostic DTOs use
    /// ordinary RPC copying when returning message buffers.
    /// </remarks>
    public required TMessage Message { get; init; }

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

internal sealed class MessagingDiagnostics(Inbox inbox) : IMessagingDiagnostics
{
    public IReadOnlyList<DeadLetter<InboxMessage>> InboxDeadLetters =>
        inbox.DeadLetterState.Values.Select(static entry => new DeadLetter<InboxMessage>
        {
            Message = entry.Message,
            DeadLetteredAt = entry.DeadLetteredAt,
            Reason = entry.Reason,
            AttemptCount = entry.AttemptCount
        }).ToList();

    public bool RemoveInboxDeadLetter(HierarchicalKey messageId) => inbox.RemoveDeadLetter(messageId);
}
