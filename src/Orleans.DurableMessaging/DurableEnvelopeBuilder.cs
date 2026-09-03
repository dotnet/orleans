using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
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
/// The builder implements <see cref="IBufferWriter{T}"/> to enable direct serialization into a shared buffer.
/// All data (body and context values) is stored in a single <see cref="ArcBufferWriter"/> with offset/length
/// indices for each value, following the MigrationContext pattern from Orleans.Core.
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
    internal const string RequestContextKey = "$orleans.request-context";
    private const string CallChainReentrancyRequestContextKey = "#CCR";
    private const string PingRequestContextKey = "Ping";
    private const string TurnIsolationRequestContextKey = "Orleans.DurableJobs.TurnIsolation";
    private const int MaxRequestContextEntryCount = 32;
    private const int MaxRequestContextKeyLength = 256;
    private const int MaxSerializedRequestContextValueLength = 64 * 1024;
    private const int MaxSerializedRequestContextTotalLength = 256 * 1024;

    // Reflection cache for setting private DurableEnvelopeData fields
    private static readonly FieldInfo BufferField = typeof(DurableEnvelopeData).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo BodySliceField = typeof(DurableEnvelopeData).GetField("_bodySlice", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ContextIndicesField = typeof(DurableEnvelopeData).GetField("_contextIndices", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Internal properties injected by IInboxHandlerContext implementation
    internal SerializerSessionPool SessionPool { get; init; } = null!;
    internal GrainId SenderId { get; init; }

    private GrainId _receiverId;
    private string _routeKey = string.Empty;
    private HierarchicalKey? _correlationKey;
    private GrainId? _replyTo;

    // MigrationContext-style keyed context storage
    private Dictionary<string, (int Offset, int Length)>? _contextIndices;
    private ArcBufferWriter _buffer = new();
    private (int Offset, int Length) _bodySlice;
    private bool _bodyWritten;
    private bool _invalid;

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
    /// <exception cref="ArgumentException">Thrown if <paramref name="routeKey"/> is empty or whitespace.</exception>
    /// <example>
    /// <code>
    /// builder.To(targetGrain, "account.debit");
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder To(GrainId target, string routeKey)
    {
        ArgumentNullException.ThrowIfNull(routeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);

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
        if (_bodyWritten)
        {
            throw new InvalidOperationException("Body has already been set.");
        }

        ThrowIfInvalid();
        try
        {
            var startOffset = _buffer.Length;
            using var session = SessionPool.GetSession();
            var writer = Writer.Create((IBufferWriter<byte>)this, session);
            SessionPool.CodecProvider.GetCodec<T>().WriteField(ref writer, 0, typeof(T), body);
            writer.Commit();
            _bodySlice = (startOffset, _buffer.Length - startOffset);
            _bodyWritten = true;
        }
        catch
        {
            Invalidate();
            throw;
        }

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
        ArgumentNullException.ThrowIfNull(correlationKey);
        _correlationKey = HierarchicalKey.Create(correlationKey);
        return this;
    }

    /// <summary>
    /// Sets the reply-to grain for durable RPC callbacks.
    /// </summary>
    /// <param name="replyTo">The grain to receive the reply.</param>
    /// <returns>This builder for chaining.</returns>
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
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.Equals(key, RequestContextKey, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The context key '{RequestContextKey}' is reserved for Orleans request context propagation.",
                nameof(key));
        }

        return WithContextValueCore(key, value);
    }

    private DurableEnvelopeBuilder WithContextValueCore<T>(string key, T value)
    {
        ThrowIfInvalid();
        _contextIndices ??= new(StringComparer.Ordinal);

        if (_contextIndices.ContainsKey(key))
        {
            throw new InvalidOperationException($"Context key '{key}' has already been set.");
        }

        try
        {
            var startOffset = _buffer.Length;
            using var session = SessionPool.GetSession();
            var writer = Writer.Create((IBufferWriter<byte>)this, session);
            SessionPool.CodecProvider.GetCodec<T>().WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            _contextIndices[key] = (startOffset, _buffer.Length - startOffset);
        }
        catch
        {
            Invalidate();
            throw;
        }

        return this;
    }

    /// <summary>
    /// Adds every value from the current Orleans request context to the envelope.
    /// </summary>
    /// <returns>This builder for chaining.</returns>
    public DurableEnvelopeBuilder WithCurrentRequestContext()
    {
        var values = RequestContext.Entries
            .Where(static entry => !IsFrameworkContextKey(entry.Key))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        ValidateRequestContextValues(values, SessionPool);
        if (values.Count > 0)
        {
            WithContextValueCore(RequestContextKey, values);
        }

        return this;
    }

    internal static void ValidateRequestContextValues(
        IReadOnlyDictionary<string, object> values,
        SerializerSessionPool sessionPool)
    {
        if (values.Count > MaxRequestContextEntryCount)
        {
            throw new InvalidOperationException(
                $"Durable envelope request context exceeds the limit of {MaxRequestContextEntryCount} entries.");
        }

        var totalLength = 0;
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxRequestContextKeyLength)
            {
                throw new InvalidOperationException(
                    $"Durable envelope request context keys must contain between 1 and {MaxRequestContextKeyLength} characters.");
            }

            var serializedLength = GetSerializedLength(value, sessionPool);
            if (serializedLength > MaxSerializedRequestContextValueLength)
            {
                throw new InvalidOperationException(
                    $"Serialized durable envelope request context value '{key}' exceeds the "
                    + $"{MaxSerializedRequestContextValueLength}-byte limit.");
            }

            totalLength = checked(totalLength + serializedLength);
            if (totalLength > MaxSerializedRequestContextTotalLength)
            {
                throw new InvalidOperationException(
                    $"Serialized durable envelope request context exceeds the "
                    + $"{MaxSerializedRequestContextTotalLength}-byte total limit.");
            }
        }

    }

    private static int GetSerializedLength(object value, SerializerSessionPool sessionPool)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var session = sessionPool.GetSession();
        var writer = Writer.Create(buffer, session);
        sessionPool.CodecProvider.GetCodec<object>().WriteField(ref writer, 0, typeof(object), value);
        writer.Commit();
        return buffer.WrittenCount;
    }

    internal static bool IsFrameworkContextKey(string key) =>
        string.Equals(key, CallChainReentrancyRequestContextKey, StringComparison.Ordinal)
        || string.Equals(key, PingRequestContextKey, StringComparison.Ordinal)
        || string.Equals(key, TurnIsolationRequestContextKey, StringComparison.Ordinal);

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
        ThrowIfInvalid();
        if (!_bodyWritten)
        {
            throw new InvalidOperationException("Message body must be set via WithBody<T>().");
        }

        if (string.IsNullOrEmpty(_routeKey))
        {
            throw new InvalidOperationException("Target and route key must be set via To().");
        }

        ArcBuffer buffer;
        try
        {
            buffer = _buffer.Length > 0 ? _buffer.ConsumeSlice(_buffer.Length) : default;
        }
        finally
        {
            _buffer.Dispose();
        }

        try
        {
            var data = new DurableEnvelopeData(SessionPool);
            BufferField.SetValue(data, buffer);
            BodySliceField.SetValue(data, _bodySlice);
            if (_contextIndices is not null)
            {
                ContextIndicesField.SetValue(data, _contextIndices);
            }

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
        catch
        {
            buffer.Dispose();
            throw;
        }
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
        _buffer.Dispose();
        _buffer = new ArcBufferWriter();
        _bodySlice = default;
        _bodyWritten = false;
        _invalid = false;
    }

    private void ThrowIfInvalid()
    {
        if (_invalid)
        {
            throw new InvalidOperationException("The durable envelope builder cannot be reused after serialization fails.");
        }
    }

    private void Invalidate()
    {
        _invalid = true;
        _buffer.Dispose();
    }

    // IBufferWriter<byte> implementation for serialization
    void IBufferWriter<byte>.Advance(int count) => ((IBufferWriter<byte>)_buffer).Advance(count);
    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => ((IBufferWriter<byte>)_buffer).GetMemory(sizeHint);
    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => ((IBufferWriter<byte>)_buffer).GetSpan(sizeHint);
}
