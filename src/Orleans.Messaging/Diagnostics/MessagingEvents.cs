using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Messaging.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for durable messaging events.
/// </summary>
/// <remarks>
/// <para>
/// These types are public but may change between minor versions. They are intended for advanced scenarios such as
/// simulation testing and diagnostics.
/// </para>
/// <para>
/// Each event is written synchronously on the activation which raises it, at the transition which it describes, so an
/// observer sees the grain's state at that transition. An exception thrown by an observer propagates to the code which
/// raised the event; <see cref="InboxMessageReceiving"/> documents how a delivery treats one.
/// </para>
/// </remarks>
public static class MessagingEvents
{
    /// <summary>The name of the diagnostic listener for durable messaging events.</summary>
    public const string ListenerName = "Orleans.Messaging";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>Gets an observable sequence of all durable messaging events.</summary>
    public static IObservable<MessagingEvent> AllEvents { get; } = new Observable();

    /// <summary>The base class used for durable messaging diagnostic events.</summary>
    /// <param name="grainContext">The activation whose inbox or outbox raised the event.</param>
    public abstract class MessagingEvent(IGrainContext grainContext)
    {
        /// <summary>The activation whose inbox or outbox raised the event.</summary>
        public readonly IGrainContext GrainContext = grainContext;
    }

    /// <summary>
    /// Event payload for a delivery attempt which reached a grain's inbox and isn't a duplicate, before the inbox stages
    /// anything for the message.
    /// </summary>
    /// <param name="grainContext">The receiving activation.</param>
    /// <param name="message">The delivered message.</param>
    /// <remarks>
    /// An exception thrown by an observer fails the delivery attempt like an inbox message sink exception: the inbox
    /// stages nothing, and the sender's outbox retries the message, which tests use to inject delivery faults.
    /// </remarks>
    public sealed class InboxMessageReceiving(IGrainContext grainContext, InboxMessage message) : MessagingEvent(grainContext)
    {
        /// <summary>The delivered message.</summary>
        public readonly InboxMessage Message = message;
    }

    /// <summary>
    /// Event payload for a failed delivery attempt whose retry the outbox staged.
    /// </summary>
    /// <param name="grainContext">The sending activation.</param>
    /// <param name="messageId">The message's ID.</param>
    /// <param name="receiverId">The receiving grain.</param>
    /// <param name="attemptCount">The number of failed delivery attempts so far.</param>
    /// <param name="nextAttemptAt">The time, on the Durable Jobs clock, from which the outbox attempts the delivery again.</param>
    /// <param name="error">The attempt's failure.</param>
    public sealed class OutboxRetryScheduled(
        IGrainContext grainContext,
        MessageId messageId,
        GrainId receiverId,
        int attemptCount,
        DateTimeOffset nextAttemptAt,
        string error) : MessagingEvent(grainContext)
    {
        /// <summary>The message's ID.</summary>
        public readonly MessageId MessageId = messageId;

        /// <summary>The receiving grain.</summary>
        public readonly GrainId ReceiverId = receiverId;

        /// <summary>The number of failed delivery attempts so far.</summary>
        public readonly int AttemptCount = attemptCount;

        /// <summary>The time, on the Durable Jobs clock, from which the outbox attempts the delivery again.</summary>
        public readonly DateTimeOffset NextAttemptAt = nextAttemptAt;

        /// <summary>The attempt's failure.</summary>
        public readonly string Error = error;
    }

    /// <summary>
    /// Event payload for an outbox message whose retirement the outbox staged: the message was delivered, its receiver
    /// rejected it, or the outbox dead-lettered it.
    /// </summary>
    /// <param name="grainContext">The sending activation.</param>
    /// <param name="messageId">The message's ID.</param>
    /// <param name="retirement">How the message retired.</param>
    /// <remarks>
    /// The retirement commits with the sending grain's next journal write. A reference message's source is notified
    /// first, so its changes for the retirement are staged when observers run.
    /// </remarks>
    public sealed class OutboxMessageRetired(
        IGrainContext grainContext,
        MessageId messageId,
        OutboxMessageRetirement retirement) : MessagingEvent(grainContext)
    {
        /// <summary>The message's ID.</summary>
        public readonly MessageId MessageId = messageId;

        /// <summary>How the message retired.</summary>
        public readonly OutboxMessageRetirement Retirement = retirement;
    }

    internal static void EmitInboxMessageReceiving(IGrainContext grainContext, InboxMessage message)
    {
        if (!Listener.IsEnabled(nameof(InboxMessageReceiving)))
        {
            return;
        }

        Emit(grainContext, message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, InboxMessage message) =>
            Listener.Write(nameof(InboxMessageReceiving), new InboxMessageReceiving(grainContext, message));
    }

    internal static void EmitOutboxRetryScheduled(
        IGrainContext grainContext,
        MessageId messageId,
        GrainId receiverId,
        int attemptCount,
        DateTimeOffset nextAttemptAt,
        string error)
    {
        if (!Listener.IsEnabled(nameof(OutboxRetryScheduled)))
        {
            return;
        }

        Emit(grainContext, messageId, receiverId, attemptCount, nextAttemptAt, error);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            IGrainContext grainContext,
            MessageId messageId,
            GrainId receiverId,
            int attemptCount,
            DateTimeOffset nextAttemptAt,
            string error) =>
            Listener.Write(
                nameof(OutboxRetryScheduled),
                new OutboxRetryScheduled(grainContext, messageId, receiverId, attemptCount, nextAttemptAt, error));
    }

    internal static void EmitOutboxMessageRetired(IGrainContext grainContext, MessageId messageId, OutboxMessageRetirement retirement)
    {
        if (!Listener.IsEnabled(nameof(OutboxMessageRetired)))
        {
            return;
        }

        Emit(grainContext, messageId, retirement);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, MessageId messageId, OutboxMessageRetirement retirement) =>
            Listener.Write(nameof(OutboxMessageRetired), new OutboxMessageRetired(grainContext, messageId, retirement));
    }

    private sealed class Observable : IObservable<MessagingEvent>
    {
        public IDisposable Subscribe(IObserver<MessagingEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<MessagingEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();

            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is MessagingEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
