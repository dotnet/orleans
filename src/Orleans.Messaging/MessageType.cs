using System;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Messaging;

/// <summary>
/// Binds an exact message subject to an Orleans payload serializer.
/// </summary>
/// <typeparam name="T">The subject's payload contract.</typeparam>
/// <remarks>
/// Bindings are immutable and can be shared across activations. Decode during handler preparation;
/// decoded resource-bearing values retain their ordinary application-managed ownership.
/// </remarks>
public sealed class MessageType<T>
{
    private readonly Serializer<T> _serializer;

    /// <summary>Creates a subject and payload-type binding.</summary>
    /// <param name="subject">The ordinal protocol subject.</param>
    /// <param name="serializer">The payload serializer.</param>
    public MessageType(string subject, Serializer<T> serializer)
    {
        EnvelopeValidation.ValidateSubject(subject);
        ArgumentNullException.ThrowIfNull(serializer);
        Subject = subject;
        _serializer = serializer;
    }

    /// <summary>Creates a subject and payload-type binding using the payload serializer from <paramref name="serializer"/>.</summary>
    /// <param name="subject">The ordinal protocol subject.</param>
    /// <param name="serializer">The serializer which provides the payload serializer.</param>
    /// <remarks>
    /// Use this overload to define a protocol's bindings together in one application class, constructed from the
    /// <see cref="Serializer"/> service.
    /// </remarks>
    public MessageType(string subject, Serializer serializer)
        : this(subject, (serializer ?? throw new ArgumentNullException(nameof(serializer))).GetSerializer<T>())
    {
    }

    /// <summary>Gets the exact protocol subject.</summary>
    public string Subject { get; }

    /// <summary>Decodes an envelope using this subject's payload contract.</summary>
    /// <param name="envelope">The envelope containing the serialized payload.</param>
    /// <returns>The nonnull decoded payload.</returns>
    /// <exception cref="ArgumentException">The subject is absent or different, or the payload is a serialized null value.</exception>
    public T Decode(Envelope envelope)
    {
        if (envelope.Subject is null)
        {
            throw new ArgumentException(
                $"Message subject '{Subject}' is required.", nameof(envelope));
        }
        if (!string.Equals(Subject, envelope.Subject, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Expected message subject '{Subject}', but received '{envelope.Subject}'.",
                nameof(envelope));
        }

        return _serializer.Deserialize(envelope.Payload)
            ?? throw new ArgumentException($"Message subject '{Subject}' requires a nonnull payload.", nameof(envelope));
    }

    /// <summary>Encodes a typed body into an outgoing message.</summary>
    /// <param name="messageId">The stable application command identity.</param>
    /// <param name="receiverId">The destination inbox.</param>
    /// <param name="body">The nonnull body to serialize.</param>
    /// <param name="deliverAt">
    /// The optional earliest time, on the Durable Jobs clock, at which the receiving inbox processes the message.
    /// See <see cref="Envelope.DeliverAt"/>.
    /// </param>
    /// <returns>An outgoing message with the encoded payload.</returns>
    /// <remarks>
    /// Serialization completes before publication. Keep the published payload bytes unchanged.
    /// </remarks>
    public OutboxMessage Create(MessageId messageId, GrainId receiverId, T body, DateTimeOffset? deliverAt = null)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        if (receiverId.IsDefault)
        {
            throw new ArgumentException("The receiving grain ID must be set.", nameof(receiverId));
        }
        EnvelopeValidation.ValidateMessageId(messageId);
        var payload = _serializer.SerializeToArray(body);
        return new OutboxMessage
        {
            Envelope = new Envelope
            {
                MessageId = messageId,
                Subject = Subject,
                Payload = payload,
                DeliverAt = deliverAt
            },
            ReceiverId = receiverId
        };
    }
}
