using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>Extensions for encoding and staging typed messages and replies.</summary>
public static class OutboxExtensions
{
    /// <summary>Encodes and synchronously stages one typed command.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="outbox">The owning grain's outbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="messageId">The stable application command identity.</param>
    /// <param name="destination">The destination inbox.</param>
    /// <param name="body">The body to encode.</param>
    /// <param name="deliverAt">
    /// The optional earliest time, on the Durable Jobs clock, at which the receiving inbox processes the message.
    /// The outbox still delivers the message immediately. See <see cref="Envelope.DeliverAt"/>.
    /// </param>
    /// <remarks>
    /// Serialization finishes before staging begins. Compute fallible business results first,
    /// then send, apply the business state, and complete in one synchronous final block.
    /// Payload and header arrays remain available through the staged message's ordinary managed references.
    /// </remarks>
    public static void Send<T>(
        this IOutbox outbox, MessageType<T> messageType,
        MessageId messageId, GrainId destination, T body, DateTimeOffset? deliverAt = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messageType);
        var message = messageType.Create(messageId, destination, body, deliverAt);
        outbox.Send(message);
    }

    /// <summary>Encodes and synchronously stages one typed command to a grain reference.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="outbox">The owning grain's outbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="messageId">The stable application command identity.</param>
    /// <param name="destination">The destination grain.</param>
    /// <param name="body">The body to encode.</param>
    /// <param name="deliverAt">
    /// The optional earliest time, on the Durable Jobs clock, at which the receiving inbox processes the message.
    /// The outbox still delivers the message immediately. See <see cref="Envelope.DeliverAt"/>.
    /// </param>
    public static void Send<T>(
        this IOutbox outbox, MessageType<T> messageType,
        MessageId messageId, IAddressable destination, T body, DateTimeOffset? deliverAt = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        outbox.Send(messageType, messageId, destination.GetGrainId(), body, deliverAt);
    }

    /// <summary>Encodes and synchronously stages typed commands in enumeration order.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="outbox">The owning grain's outbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="messages">The application-defined identities, destinations, and bodies.</param>
    /// <remarks>
    /// Enumerates once and sends each command using the ordinary typed send helper.
    /// If enumeration, encoding, or staging fails, earlier messages remain staged
    /// under their original identities with their encoded payload and headers.
    /// Retry with the same identities and content; persistence follows the grain's
    /// ordinary journal write.
    /// </remarks>
    public static void Send<T>(
        this IOutbox outbox, MessageType<T> messageType,
        IEnumerable<(MessageId MessageId, GrainId Destination, T Body)> messages)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var (messageId, destination, body) in messages)
        {
            outbox.Send(messageType, messageId, destination, body);
        }
    }

    /// <summary>Encodes and stages a reply under the received command's deterministic result child.</summary>
    /// <typeparam name="T">The reply body contract.</typeparam>
    /// <param name="outbox">The replying grain's outbox.</param>
    /// <param name="messageType">The reply subject and serializer binding.</param>
    /// <param name="context">The current received message and completion context.</param>
    /// <param name="destination">The application-selected reply destination.</param>
    /// <param name="body">The reply body.</param>
    /// <remarks>The reply identity is <c>context.Message.Envelope.MessageId.Key.CreateChildKey("result")</c>.</remarks>
    public static void SendReply<T>(
        this IOutbox outbox, MessageType<T> messageType,
        IInboxHandlerContext context, GrainId destination, T body)
    {
        ArgumentNullException.ThrowIfNull(context);
        outbox.Send(messageType, new MessageId(context.Message.Envelope.MessageId.Key.CreateChildKey("result")), destination, body);
    }

    /// <summary>Encodes and stages a deterministic result reply to a grain reference.</summary>
    /// <typeparam name="T">The reply body contract.</typeparam>
    /// <param name="outbox">The replying grain's outbox.</param>
    /// <param name="messageType">The reply subject and serializer binding.</param>
    /// <param name="context">The current received message and completion context.</param>
    /// <param name="destination">The application-selected replying grain reference.</param>
    /// <param name="body">The reply body.</param>
    public static void SendReply<T>(
        this IOutbox outbox, MessageType<T> messageType,
        IInboxHandlerContext context, IAddressable destination, T body)
    {
        ArgumentNullException.ThrowIfNull(destination);
        outbox.SendReply(messageType, context, destination.GetGrainId(), body);
    }
}
