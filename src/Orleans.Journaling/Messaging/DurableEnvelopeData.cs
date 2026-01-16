using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Opaque data storage for envelope body and request context.
/// Modeled after MigrationContext's deferred serialization pattern with keyed indices.
/// Body and all RequestContext values share the same underlying ArcBuffer.
/// </summary>
/// <remarks>
/// This design enables:
/// <list type="bullet">
///   <item><description>Deferred deserialization: Body and context values are only deserialized when accessed</description></item>
///   <item><description>Zero-copy slicing: Body and all context values share the same underlying ArcBuffer</description></item>
///   <item><description>Error isolation: Deserialization failures don't crash the grain; they can be handled gracefully</description></item>
///   <item><description>Per-key context access: Individual context values can be retrieved independently</description></item>
/// </list>
/// </remarks>
[GenerateSerializer, Immutable]
public sealed class DurableEnvelopeData : IDisposable
{
    [NonSerialized]
    private readonly SerializerSessionPool? _sessionPool;

    /// <summary>
    /// Shared buffer containing body and all request context values.
    /// </summary>
    [Id(0), Immutable]
    private ArcBuffer _buffer;

    /// <summary>
    /// Offset and length of the body within the buffer.
    /// </summary>
    [Id(1)]
    private (int Offset, int Length) _bodySlice;

    /// <summary>
    /// Keyed indices for request context values within the buffer.
    /// Each key maps to its own (Offset, Length) slice, allowing independent deserialization.
    /// </summary>
    [Id(2), Immutable]
    private Dictionary<string, (int Offset, int Length)>? _contextIndices;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableEnvelopeData"/> class.
    /// </summary>
    /// <param name="sessionPool">The serializer session pool for serialization/deserialization.</param>
    [GeneratedActivatorConstructor]
    public DurableEnvelopeData(SerializerSessionPool sessionPool)
    {
        _sessionPool = sessionPool;
    }

    /// <summary>
    /// Gets the keys of all stored request context values.
    /// </summary>
    public IEnumerable<string> ContextKeys => _contextIndices?.Keys ?? Enumerable.Empty<string>();

    /// <summary>
    /// Returns true if a request context value exists for the specified key.
    /// </summary>
    /// <param name="key">The context key to check.</param>
    /// <returns>True if the key exists in the context; otherwise, false.</returns>
    public bool HasContextKey(string key) => _contextIndices?.ContainsKey(key) ?? false;

    /// <summary>
    /// Attempts to deserialize the body as the specified type.
    /// Returns false if deserialization fails (type mismatch, corruption, etc.).
    /// </summary>
    /// <typeparam name="T">The type to deserialize the body as.</typeparam>
    /// <param name="value">The deserialized value, or default if deserialization fails.</param>
    /// <returns>True if deserialization succeeded; otherwise, false.</returns>
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
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(slice.AsReadOnlySequence(), session);
            var field = reader.ReadFieldHeader();
            value = _sessionPool.CodecProvider.GetCodec<T>().ReadValue(ref reader, field);
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Attempts to deserialize a specific request context value.
    /// Returns false if the key doesn't exist or deserialization fails.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the context value as.</typeparam>
    /// <param name="key">The context key to retrieve.</param>
    /// <param name="value">The deserialized value, or default if not found or deserialization fails.</param>
    /// <returns>True if the key exists and deserialization succeeded; otherwise, false.</returns>
    public bool TryGetContextValue<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        if (_sessionPool is null || _contextIndices is null || !_contextIndices.TryGetValue(key, out var slice))
        {
            value = default;
            return false;
        }

        try
        {
            var buffer = _buffer.Slice(slice.Offset, slice.Length);
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(buffer.AsReadOnlySequence(), session);
            var field = reader.ReadFieldHeader();
            value = _sessionPool.CodecProvider.GetCodec<T>().ReadValue(ref reader, field);
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Gets the raw body bytes for forwarding without deserialization.
    /// </summary>
    /// <returns>A read-only sequence containing the raw body bytes.</returns>
    public ReadOnlySequence<byte> GetBodyBytes()
        => _buffer.Slice(_bodySlice.Offset, _bodySlice.Length).AsReadOnlySequence();

    /// <summary>
    /// Gets the raw bytes for a specific context key for forwarding without deserialization.
    /// </summary>
    /// <param name="key">The context key to retrieve.</param>
    /// <param name="value">The raw bytes for the context value, or default if not found.</param>
    /// <returns>True if the key exists; otherwise, false.</returns>
    public bool TryGetContextBytes(string key, out ReadOnlySequence<byte> value)
    {
        if (_contextIndices is not null && _contextIndices.TryGetValue(key, out var slice))
        {
            value = _buffer.Slice(slice.Offset, slice.Length).AsReadOnlySequence();
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Disposes the underlying buffer, releasing any held resources.
    /// </summary>
    public void Dispose() => _buffer.Dispose();
}
