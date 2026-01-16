using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Opaque data storage for envelope body and request context.
/// Modeled after MigrationContext's deferred serialization pattern.
/// Both Body and RequestContext can point to the same underlying ArcBuffer.
/// </summary>
[GenerateSerializer]
public sealed class DurableEnvelopeData : IDisposable
{
    [NonSerialized]
    private readonly SerializerSessionPool? _sessionPool;

    /// <summary>
    /// Shared buffer containing both body and request context data.
    /// </summary>
    [Id(0), Immutable]
    private ArcBuffer _buffer;

    /// <summary>
    /// Offset and length of the body within the buffer.
    /// </summary>
    [Id(1)]
    private (int Offset, int Length) _bodySlice;

    /// <summary>
    /// Offset and length of the request context within the buffer.
    /// </summary>
    [Id(2)]
    private (int Offset, int Length) _requestContextSlice;

    /// <summary>
    /// Gets a value indicating whether this instance has body data.
    /// </summary>
    public bool HasBody => _bodySlice.Length > 0;

    /// <summary>
    /// Gets a value indicating whether this instance has request context data.
    /// </summary>
    public bool HasRequestContext => _requestContextSlice.Length > 0;

    /// <summary>
    /// Gets the total length of the buffer in bytes.
    /// </summary>
    public int BufferLength => _buffer.Length;

    /// <summary>
    /// Constructor for Orleans serialization.
    /// </summary>
    [GeneratedActivatorConstructor]
    public DurableEnvelopeData(SerializerSessionPool sessionPool)
    {
        _sessionPool = sessionPool;
    }

    /// <summary>
    /// Creates envelope data from a body object and optional request context.
    /// Serializes both into a shared buffer.
    /// </summary>
    /// <typeparam name="TBody">The type of the message body.</typeparam>
    /// <param name="sessionPool">The serializer session pool.</param>
    /// <param name="body">The message body to serialize.</param>
    /// <param name="requestContext">Optional request context to serialize.</param>
    /// <returns>A new <see cref="DurableEnvelopeData"/> instance.</returns>
    public static DurableEnvelopeData Create<TBody>(
        SerializerSessionPool sessionPool,
        TBody body,
        Dictionary<string, object?>? requestContext = null)
    {
        var data = new DurableEnvelopeData(sessionPool);
        using var writer = new ArcBufferWriter();

        // Serialize body
        var bodyStart = writer.Length;
        using (var session = sessionPool.GetSession())
        {
            var serializer = Writer.Create((IBufferWriter<byte>)writer, session);
            if (sessionPool.CodecProvider.TryGetCodec<TBody>() is { } codec)
            {
                codec.WriteField(ref serializer, 0, typeof(TBody), body);
                serializer.Commit();
            }
            else
            {
                throw new InvalidOperationException($"No codec found for type {typeof(TBody).FullName}");
            }
        }
        data._bodySlice = (bodyStart, writer.Length - bodyStart);

        // Serialize request context if present
        if (requestContext is { Count: > 0 })
        {
            var ctxStart = writer.Length;
            using (var session = sessionPool.GetSession())
            {
                var serializer = Writer.Create((IBufferWriter<byte>)writer, session);
                if (sessionPool.CodecProvider.TryGetCodec<Dictionary<string, object?>>() is { } codec)
                {
                    codec.WriteField(ref serializer, 0, typeof(Dictionary<string, object?>), requestContext);
                    serializer.Commit();
                }
            }
            data._requestContextSlice = (ctxStart, writer.Length - ctxStart);
        }

        data._buffer = writer.ConsumeSlice(writer.Length);
        return data;
    }

    /// <summary>
    /// Creates envelope data from raw bytes (for forwarding without deserialization).
    /// </summary>
    /// <param name="sessionPool">The serializer session pool.</param>
    /// <param name="bodyBytes">Raw body bytes.</param>
    /// <param name="requestContextBytes">Raw request context bytes (optional).</param>
    /// <returns>A new <see cref="DurableEnvelopeData"/> instance.</returns>
    public static DurableEnvelopeData CreateFromBytes(
        SerializerSessionPool sessionPool,
        ReadOnlySpan<byte> bodyBytes,
        ReadOnlySpan<byte> requestContextBytes = default)
    {
        var data = new DurableEnvelopeData(sessionPool);
        using var writer = new ArcBufferWriter();

        // Write body bytes
        var bodyStart = writer.Length;
        writer.Write(bodyBytes);
        data._bodySlice = (bodyStart, bodyBytes.Length);

        // Write request context bytes if present
        if (requestContextBytes.Length > 0)
        {
            var ctxStart = writer.Length;
            writer.Write(requestContextBytes);
            data._requestContextSlice = (ctxStart, requestContextBytes.Length);
        }

        data._buffer = writer.ConsumeSlice(writer.Length);
        return data;
    }

    /// <summary>
    /// Attempts to deserialize the body as the specified type.
    /// Returns false if deserialization fails (type mismatch, corruption, etc.).
    /// </summary>
    /// <typeparam name="T">The expected type of the body.</typeparam>
    /// <param name="value">When this method returns true, contains the deserialized body.</param>
    /// <returns>true if deserialization succeeded; otherwise, false.</returns>
    public bool TryGetBody<T>([MaybeNullWhen(false)] out T value)
    {
        if (_sessionPool is null || _bodySlice.Length == 0)
        {
            value = default;
            return false;
        }

        try
        {
            var slice = _buffer.Slice(_bodySlice.Offset, _bodySlice.Length);
            try
            {
                using var session = _sessionPool.GetSession();
                var reader = Reader.Create(slice.AsReadOnlySequence(), session);
                var field = reader.ReadFieldHeader();
                if (_sessionPool.CodecProvider.TryGetCodec<T>() is { } codec)
                {
                    value = codec.ReadValue(ref reader, field);
                    return value is not null;
                }
            }
            finally
            {
                slice.Dispose();
            }
        }
        catch
        {
            // Deserialization failed - type mismatch, corruption, etc.
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Attempts to deserialize the request context.
    /// </summary>
    /// <param name="value">When this method returns true, contains the deserialized request context.</param>
    /// <returns>true if deserialization succeeded; otherwise, false.</returns>
    public bool TryGetRequestContext([MaybeNullWhen(false)] out Dictionary<string, object?> value)
    {
        if (_sessionPool is null || _requestContextSlice.Length == 0)
        {
            value = default;
            return false;
        }

        try
        {
            var slice = _buffer.Slice(_requestContextSlice.Offset, _requestContextSlice.Length);
            try
            {
                using var session = _sessionPool.GetSession();
                var reader = Reader.Create(slice.AsReadOnlySequence(), session);
                var field = reader.ReadFieldHeader();
                if (_sessionPool.CodecProvider.TryGetCodec<Dictionary<string, object?>>() is { } codec)
                {
                    value = codec.ReadValue(ref reader, field);
                    return value is not null;
                }
            }
            finally
            {
                slice.Dispose();
            }
        }
        catch
        {
            // Deserialization failed
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets the raw body bytes for forwarding without deserialization.
    /// </summary>
    /// <returns>A read-only sequence containing the raw body bytes.</returns>
    public ReadOnlySequence<byte> GetBodyBytes()
    {
        if (_bodySlice.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        return _buffer.Slice(_bodySlice.Offset, _bodySlice.Length).AsReadOnlySequence();
    }

    /// <summary>
    /// Gets the raw request context bytes for forwarding without deserialization.
    /// </summary>
    /// <returns>A read-only sequence containing the raw request context bytes.</returns>
    public ReadOnlySequence<byte> GetRequestContextBytes()
    {
        if (_requestContextSlice.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        return _buffer.Slice(_requestContextSlice.Offset, _requestContextSlice.Length).AsReadOnlySequence();
    }

    /// <summary>
    /// Releases the buffer resources.
    /// </summary>
    public void Dispose() => _buffer.Dispose();
}
