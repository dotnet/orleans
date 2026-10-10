using System;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Binds an exact message subject to an Orleans payload serializer.
/// </summary>
/// <typeparam name="T">The subject's payload contract.</typeparam>
/// <remarks>
/// Bindings are immutable and can be shared across activations. Decode during handler preparation;
/// decoded resource-bearing values retain their ordinary application-managed ownership.
/// </remarks>
public sealed class DurableMessageType<T>
{
    private readonly Serializer<T> _serializer;

    /// <summary>Creates a subject and payload-type binding.</summary>
    /// <param name="subject">The ordinal protocol subject.</param>
    /// <param name="serializer">The payload serializer.</param>
    public DurableMessageType(string subject, Serializer<T> serializer)
    {
        DurableEnvelopeValidation.ValidateSubject(subject);
        ArgumentNullException.ThrowIfNull(serializer);
        Subject = subject;
        _serializer = serializer;
    }

    /// <summary>Gets the exact protocol subject.</summary>
    public string Subject { get; }

    /// <summary>Decodes a borrowed envelope using this subject's payload contract.</summary>
    /// <param name="envelope">The envelope, kept alive through decoding.</param>
    /// <returns>The nonnull decoded payload.</returns>
    /// <exception cref="ArgumentException">The envelope has a different subject or a serialized null payload.</exception>
    public T Decode(DurableEnvelope envelope)
    {
        if (!string.Equals(Subject, envelope.Subject, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Expected durable message subject '{Subject}', but received '{envelope.Subject}'.", nameof(envelope));
        }

        return _serializer.Deserialize(envelope.Payload)
            ?? throw new ArgumentException($"Durable message subject '{Subject}' requires a nonnull payload.", nameof(envelope));
    }

    /// <summary>Encodes a typed body into an independently owned immutable envelope.</summary>
    /// <param name="messageId">The stable application command identity.</param>
    /// <param name="senderId">The sending grain identity.</param>
    /// <param name="receiverId">The destination inbox.</param>
    /// <param name="body">The nonnull body to serialize.</param>
    /// <returns>An envelope which must be disposed by its owner.</returns>
    /// <remarks>
    /// Encoding borrows a process-shared pooled buffer and transfers a slice to the result.
    /// Repeated calls can share backing pages while retaining independent payload ownership.
    /// </remarks>
    public DurableEnvelope Create(HierarchicalKey messageId, GrainId senderId, GrainId receiverId, T body)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        var envelope = new DurableEnvelope
        {
            MessageId = messageId,
            SenderId = senderId,
            ReceiverId = receiverId,
            Subject = Subject,
            Payload = default
        };
        DurableEnvelopeValidation.Validate(envelope);
        var buffer = DurableMessageBuffers.Pool.Get();
        try
        {
            _serializer.Serialize(body, buffer);
            return envelope with { Payload = buffer.ConsumeSlice(buffer.Length) };
        }
        catch
        {
            buffer.Reset();
            throw;
        }
        finally
        {
            DurableMessageBuffers.Pool.Return(buffer);
        }
    }
}
