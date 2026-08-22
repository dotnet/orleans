using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.DurableMessaging;

/// <summary>
/// Builder for creating durable envelopes with fluent configuration.
/// Use <see cref="WithBody{T}"/> to set the message body, then <see cref="Build"/> to create the envelope.
/// Context values are serialized independently (MigrationContext pattern) for per-key access.
/// </summary>
/// <remarks>
/// <para>
/// The builder implements <see cref="IBufferWriter{T}"/> to serialize into one pooled staging buffer.
/// Building the envelope copies that data into a managed buffer with offset/length indices for each value.
/// </para>
/// <para>
/// Usage example:
/// <code>
/// var envelope = context.CreateEnvelope()
///     .To(targetGrain, "transfer.debit")
///     .WithBody(new DebitRequest { Amount = 100m })
///     .WithCorrelationKey("transfer-123/debit")
///     .WithReplyTo(context.GrainId)
///     .WithContextValue("trace-id", "abc-123")
///     .Build();
/// context.Send(envelope);
/// </code>
/// </para>
/// </remarks>
public sealed class DurableEnvelopeBuilder : IBufferWriter<byte>
{
    // Internal properties injected by IInboxHandlerContext implementation
    internal SerializerSessionPool SessionPool { get; init; } = null!;
    internal GrainId SenderId { get; init; }

    private GrainId _receiverId;
    private string _routeKey = string.Empty;
    private HierarchicalKey? _correlationKey;
    private GrainId? _replyTo;

    // MigrationContext-style keyed context storage
    private Dictionary<string, (int Offset, int Length)>? _contextIndices;
    private ArrayBufferWriter<byte> _buffer = new();
    private (int Offset, int Length) _bodySlice;
    private bool _bodyWritten;
    private bool _built;

    internal DurableEnvelopeBuilder()
    {
    }

    /// <summary>
    /// Initializes a builder for a message sent by the specified grain.
    /// </summary>
    /// <param name="sessionPool">The serializer session pool used to encode the message.</param>
    /// <param name="senderId">The identity of the sending grain.</param>
    public DurableEnvelopeBuilder(SerializerSessionPool sessionPool, GrainId senderId)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);
        SessionPool = sessionPool;
        SenderId = senderId;
    }

    /// <summary>
    /// Sets the target grain and route key for this envelope.
    /// </summary>
    /// <param name="target">The target grain to receive the message.</param>
    /// <param name="routeKey">The route key for handler dispatch (e.g., "transfer.debit").</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="routeKey"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="target"/> is the default grain id or <paramref name="routeKey"/> is empty or whitespace.
    /// </exception>
    /// <example>
    /// <code>
    /// builder.To(targetGrain, "account.debit");
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder To(GrainId target, string routeKey)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(routeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        if (target.IsDefault)
        {
            throw new ArgumentException("The target grain id must not be the default value.", nameof(target));
        }

        _receiverId = target;
        _routeKey = routeKey;
        return this;
    }

    /// <summary>
    /// Sets the message body. This serializes the body immediately into the shared buffer.
    /// Can be called before or after <see cref="WithContextValue{T}"/> - order doesn't matter.
    /// </summary>
    /// <typeparam name="T">The type of the message body.</typeparam>
    /// <param name="body">The message body to serialize.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the body has already been set.</exception>
    /// <example>
    /// <code>
    /// builder.WithBody(new DebitRequest { Amount = 100m, AccountId = "acct-123" });
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder WithBody<T>(T body)
    {
        ThrowIfBuilt();
        if (_bodyWritten)
        {
            throw new InvalidOperationException("Body has already been set.");
        }

        var startOffset = _buffer.WrittenCount;
        using var session = SessionPool.GetSession();
        var writer = Writer.Create((IBufferWriter<byte>)this, session);
        SessionPool.CodecProvider.GetCodec<T>().WriteField(ref writer, 0, typeof(T), body);
        writer.Commit();
        _bodySlice = (startOffset, _buffer.WrittenCount - startOffset);
        _bodyWritten = true;

        return this;
    }

    /// <summary>
    /// Sets the hierarchical correlation key for request/response tracking.
    /// </summary>
    /// <param name="correlationKey">The correlation key (e.g., "transfer-123/debit").</param>
    /// <returns>This builder for chaining.</returns>
    /// <example>
    /// <code>
    /// // Parent request
    /// builder.WithCorrelationKey(HierarchicalKey.Create("transfer-123"));
    ///
    /// // Child request
    /// var parentKey = HierarchicalKey.Create("transfer-123");
    /// builder.WithCorrelationKey(parentKey.CreateChildKey("debit"));
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder WithCorrelationKey(HierarchicalKey correlationKey)
    {
        ThrowIfBuilt();
        _correlationKey = correlationKey;
        return this;
    }

    /// <summary>
    /// Sets the hierarchical correlation key for request/response tracking (string convenience overload).
    /// </summary>
    /// <param name="correlationKey">The correlation key as a string (e.g., "transfer-123/debit").</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="correlationKey"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="correlationKey"/> contains invalid segments.</exception>
    /// <example>
    /// <code>
    /// builder.WithCorrelationKey("transfer-123/debit");
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder WithCorrelationKey(string correlationKey)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(correlationKey);
        _correlationKey = HierarchicalKey.Create(correlationKey);
        return this;
    }

    /// <summary>
    /// Sets the destination for follow-up messages.
    /// </summary>
    /// <param name="replyTo">The grain to receive the reply.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="replyTo"/> is the default grain id.</exception>
    /// <example>
    /// <code>
    /// // Request with reply-to
    /// builder
    ///     .To(workerGrain, "process")
    ///     .WithReplyTo(context.GrainId)
    ///     .WithBody(request);
    ///
    /// // Reply in handler
    /// if (context.Envelope.ReplyTo is { } replyTo)
    /// {
    ///     var reply = context.CreateEnvelope()
    ///         .To(replyTo, "process.reply")
    ///         .WithCorrelationKey(context.Envelope.CorrelationKey)
    ///         .WithBody(response)
    ///         .Build();
    ///     context.Send(reply);
    /// }
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder WithReplyTo(GrainId replyTo)
    {
        ThrowIfBuilt();
        if (replyTo.IsDefault)
        {
            throw new ArgumentException("The reply-to grain id must not be the default value.", nameof(replyTo));
        }

        _replyTo = replyTo;
        return this;
    }

    /// <summary>
    /// Adds a typed request context value. Each value is serialized independently
    /// into the shared buffer (MigrationContext pattern), allowing per-key retrieval.
    /// Can be called before or after <see cref="WithBody{T}"/> - order doesn't matter.
    /// </summary>
    /// <typeparam name="T">The type of the context value.</typeparam>
    /// <param name="key">The context key (e.g., "trace-id", "tenant-id").</param>
    /// <param name="value">The context value to serialize.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="key"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the key has already been set.</exception>
    /// <example>
    /// <code>
    /// builder
    ///     .WithContextValue("trace-id", "abc-123")
    ///     .WithContextValue("tenant-id", "tenant-456")
    ///     .WithContextValue("user-id", userId);
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder WithContextValue<T>(string key, T value)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _contextIndices ??= new(StringComparer.Ordinal);

        if (_contextIndices.ContainsKey(key))
        {
            throw new InvalidOperationException($"Context key '{key}' has already been set.");
        }

        var startOffset = _buffer.WrittenCount;
        using var session = SessionPool.GetSession();
        var writer = Writer.Create((IBufferWriter<byte>)this, session);
        SessionPool.CodecProvider.GetCodec<T>().WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
        _contextIndices[key] = (startOffset, _buffer.WrittenCount - startOffset);

        return this;
    }

    /// <summary>
    /// Builds the durable envelope from the configured values.
    /// </summary>
    /// <returns>A new <see cref="DurableEnvelope"/> with the configured values.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the body has not been set via <see cref="WithBody{T}"/>,
    /// or if the target and route key have not been set via <see cref="To"/>.
    /// </exception>
    /// <example>
    /// <code>
    /// var envelope = context.CreateEnvelope()
    ///     .To(targetGrain, "transfer.debit")
    ///     .WithBody(new DebitRequest { Amount = 100m })
    ///     .Build();
    /// </code>
    /// </example>
    public DurableEnvelope Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("This builder has already produced an envelope.");
        }

        if (!_bodyWritten)
        {
            throw new InvalidOperationException("Message body must be set via WithBody<T>().");
        }

        if (string.IsNullOrEmpty(_routeKey))
        {
            throw new InvalidOperationException("Target and route key must be set via To().");
        }

        var buffer = _buffer.WrittenSpan.ToArray();
        _buffer = new ArrayBufferWriter<byte>();
        var data = new DurableEnvelopeData(SessionPool);
        data.Initialize(buffer, _bodySlice, _contextIndices);
        _built = true;

        return new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = SenderId,
            ReceiverId = _receiverId,
            RouteKey = _routeKey,
            CorrelationKey = _correlationKey,
            ReplyTo = _replyTo,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Resets the builder for reuse. This is typically called by pooling infrastructure.
    /// </summary>
    internal void Reset()
    {
        _receiverId = default;
        _routeKey = string.Empty;
        _correlationKey = null;
        _replyTo = null;
        _contextIndices = null;
        _buffer = new ArrayBufferWriter<byte>();
        _bodySlice = default;
        _bodyWritten = false;
        _built = false;
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("This builder has already produced an envelope.");
        }
    }

    // IBufferWriter<byte> implementation for serialization
    void IBufferWriter<byte>.Advance(int count) => _buffer.Advance(count);
    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => _buffer.GetMemory(sizeHint);
    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => _buffer.GetSpan(sizeHint);
}
