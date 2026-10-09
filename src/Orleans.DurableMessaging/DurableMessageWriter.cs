using System;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Prepares owned, typed durable envelopes using an activation's reusable encoder.
/// </summary>
/// <remarks>
/// Use synchronously in a non-reentrant activation. Prepare envelopes before shared mutations,
/// then stage them with <see cref="IDurableOutbox.Send"/> in the handler's synchronous final block.
/// Each returned envelope owns its payload and must be disposed by the caller.
/// Reentrant applications can use operation-local writers.
/// </remarks>
public sealed class DurableMessageWriter : IDisposable
{
    private readonly GrainId _senderId;
    private readonly ArcBufferWriter _encoder = new();
    private bool _disposed;

    /// <summary>Creates an encoder for the sending activation.</summary>
    /// <param name="context">The activation supplying the sender identity.</param>
    public DurableMessageWriter(IGrainContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _senderId = context.GrainId;
    }

    /// <summary>Serializes a payload and creates an independently owned envelope.</summary>
    /// <typeparam name="T">The payload contract.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="messageId">The application's stable logical command identity.</param>
    /// <param name="receiverId">The destination inbox.</param>
    /// <param name="body">The payload to encode.</param>
    /// <returns>An owned envelope, ready for synchronous outbox staging.</returns>
    public DurableEnvelope Create<T>(
        DurableMessageType<T> messageType, HierarchicalKey messageId, GrainId receiverId, T body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messageType);
        if (body is null)
        {
            throw new ArgumentNullException(nameof(body));
        }
        var envelope = new DurableEnvelope
        {
            MessageId = messageId,
            SenderId = _senderId,
            ReceiverId = receiverId,
            Subject = messageType.Subject,
            Payload = default
        };
        DurableEnvelopeValidation.Validate(envelope);
        try
        {
            messageType.Encode(body, _encoder);
            return envelope with { Payload = _encoder.ConsumeSlice(_encoder.Length) };
        }
        catch
        {
            _encoder.Reset();
            throw;
        }
    }

    /// <summary>Releases the reusable encoder's retained pages.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
    }
}
