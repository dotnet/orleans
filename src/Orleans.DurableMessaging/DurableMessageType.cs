using System;
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

    internal void Encode(T body, ArcBufferWriter writer) => _serializer.Serialize(body, writer);
}
