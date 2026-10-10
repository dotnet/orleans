using System;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>Extensions for encoding and staging typed messages and replies.</summary>
public static class DurableOutboxExtensions
{
    /// <summary>Encodes and synchronously stages one typed command.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="outbox">The owning grain's outbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="messageId">The stable application command identity.</param>
    /// <param name="destination">The destination inbox.</param>
    /// <param name="body">The body to encode.</param>
    /// <remarks>
    /// Serialization finishes before staging begins. Compute fallible business results first,
    /// then send, apply the business state, and complete in one synchronous final block.
    /// This method releases its local payload owner; durable state retains its independent slice.
    /// </remarks>
    public static void Send<T>(
        this IDurableOutbox outbox, DurableMessageType<T> messageType,
        HierarchicalKey messageId, GrainId destination, T body)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messageType);
        using var envelope = messageType.Create(messageId, outbox.SenderId, destination, body);
        outbox.Send(envelope);
    }

    /// <summary>Encodes and synchronously stages one typed command to a grain reference.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="outbox">The owning grain's outbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="messageId">The stable application command identity.</param>
    /// <param name="destination">The destination grain.</param>
    /// <param name="body">The body to encode.</param>
    public static void Send<T>(
        this IDurableOutbox outbox, DurableMessageType<T> messageType,
        HierarchicalKey messageId, IAddressable destination, T body)
    {
        ArgumentNullException.ThrowIfNull(destination);
        outbox.Send(messageType, messageId, destination.GetGrainId(), body);
    }

    /// <summary>Encodes and stages a reply under the received command's deterministic result child.</summary>
    /// <typeparam name="T">The reply body contract.</typeparam>
    /// <param name="outbox">The replying grain's outbox.</param>
    /// <param name="messageType">The reply subject and serializer binding.</param>
    /// <param name="context">The current received message and completion context.</param>
    /// <param name="destination">The application-selected reply destination.</param>
    /// <param name="body">The reply body.</param>
    /// <remarks>The reply identity is <c>context.Envelope.MessageId.CreateChildKey("result")</c>.</remarks>
    public static void SendReply<T>(
        this IDurableOutbox outbox, DurableMessageType<T> messageType,
        IInboxHandlerContext context, GrainId destination, T body)
    {
        ArgumentNullException.ThrowIfNull(context);
        outbox.Send(messageType, context.Envelope.MessageId.CreateChildKey("result"), destination, body);
    }

    /// <summary>Encodes and stages a deterministic result reply to a grain reference.</summary>
    /// <typeparam name="T">The reply body contract.</typeparam>
    /// <param name="outbox">The replying grain's outbox.</param>
    /// <param name="messageType">The reply subject and serializer binding.</param>
    /// <param name="context">The current received message and completion context.</param>
    /// <param name="destination">The application-selected replying grain reference.</param>
    /// <param name="body">The reply body.</param>
    public static void SendReply<T>(
        this IDurableOutbox outbox, DurableMessageType<T> messageType,
        IInboxHandlerContext context, IAddressable destination, T body)
    {
        ArgumentNullException.ThrowIfNull(destination);
        outbox.SendReply(messageType, context, destination.GetGrainId(), body);
    }
}
