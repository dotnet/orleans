#nullable enable
#pragma warning disable ORLEANSEXP005 // Experimental Orleans.Journaling APIs

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Benchmarks.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Journaling;
using Orleans.DurableMessaging;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Benchmarks.Journaling;

/// <summary>
/// Benchmarks handler dispatch performance for DurableInbox with capability-based routing.
/// Compares cache hit performance, cache miss performance, and validates O(1) vs O(n) behavior.
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory("Journaling")]
public class HandlerDispatchBenchmark
{
    private IDurableInbox _inbox = null!;
    private IDurableInbox _inboxWith10Handlers = null!;
    private IDurableInbox _inboxWith50Handlers = null!;
    private IDurableInbox _inboxWith100Handlers = null!;
    private IDurableInbox _dictionaryStyleInbox = null!;
    private IInboxHandlerContext _exactMatchContext = null!;
    private IInboxHandlerContext _prefixMatchContext = null!;
    private IInboxHandlerContext _noMatchContext = null!;
    private IInboxHandlerContext _lastHandlerMatchContext = null!;
    private Serializer<TestMessage> _serializer = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Setup Orleans serialization
        var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        _serializer = serviceProvider.GetRequiredService<Serializer<TestMessage>>();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();

        // Create test envelopes
        var testMessage = new TestMessage { Data = "test" };
        var exactMatchEnvelope = CreateEnvelope(_serializer, sessionPool, testMessage, "order/process");
        var prefixMatchEnvelope = CreateEnvelope(_serializer, sessionPool, testMessage, "rpc/request");
        var noMatchEnvelope = CreateEnvelope(_serializer, sessionPool, testMessage, "unknown/route");
        var lastHandlerMatchEnvelope = CreateEnvelope(_serializer, sessionPool, testMessage, "workflow/execute");

        // Create contexts
        _exactMatchContext = new MockInboxHandlerContext(exactMatchEnvelope, GrainId.Create("test", "grain1"));
        _prefixMatchContext = new MockInboxHandlerContext(prefixMatchEnvelope, GrainId.Create("test", "grain1"));
        _noMatchContext = new MockInboxHandlerContext(noMatchEnvelope, GrainId.Create("test", "grain1"));
        _lastHandlerMatchContext = new MockInboxHandlerContext(lastHandlerMatchEnvelope, GrainId.Create("test", "grain1"));

        // Setup inbox with 3 handlers (exact, prefix, fallback)
        _inbox = CreateInbox();
        _inbox.RegisterHandler(new RouteKeyHandler("order/process"));
        _inbox.RegisterHandler(new RoutePrefixHandler("rpc/"));
        _inbox.RegisterHandler(new FallbackHandler());

        // Warm up cache
        _inbox.TryFindHandler(_exactMatchContext, out _);
        _inbox.TryFindHandler(_prefixMatchContext, out _);
        _inbox.TryFindHandler(_noMatchContext, out _);

        // Setup inbox with 10 handlers
        _inboxWith10Handlers = CreateInbox();
        for (var i = 0; i < 9; i++)
        {
            _inboxWith10Handlers.RegisterHandler(new RouteKeyHandler($"route/{i}"));
        }
        _inboxWith10Handlers.RegisterHandler(new RouteKeyHandler("workflow/execute"));

        // Setup inbox with 50 handlers
        _inboxWith50Handlers = CreateInbox();
        for (var i = 0; i < 49; i++)
        {
            _inboxWith50Handlers.RegisterHandler(new RouteKeyHandler($"route/{i}"));
        }
        _inboxWith50Handlers.RegisterHandler(new RouteKeyHandler("workflow/execute"));

        // Setup inbox with 100 handlers
        _inboxWith100Handlers = CreateInbox();
        for (var i = 0; i < 99; i++)
        {
            _inboxWith100Handlers.RegisterHandler(new RouteKeyHandler($"route/{i}"));
        }
        _inboxWith100Handlers.RegisterHandler(new RouteKeyHandler("workflow/execute"));

        // Setup dictionary-style inbox (legacy pattern)
        _dictionaryStyleInbox = CreateInbox();
        _dictionaryStyleInbox.RegisterHandler("order/process", new NoOpHandler());
        _dictionaryStyleInbox.RegisterHandler("rpc/request", new NoOpHandler());
        _dictionaryStyleInbox.RegisterHandler("fallback", new NoOpHandler());

        // Warm up dictionary-style cache
        _dictionaryStyleInbox.TryFindHandler(_exactMatchContext, out _);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CacheHit")]
    public bool TryFindHandler_CacheHit_ExactMatch()
    {
        return _inbox.TryFindHandler(_exactMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("CacheHit")]
    public bool TryFindHandler_CacheHit_PrefixMatch()
    {
        return _inbox.TryFindHandler(_prefixMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("CacheHit")]
    public bool TryFindHandler_CacheHit_NoMatch()
    {
        return _inbox.TryFindHandler(_noMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("CacheMiss")]
    public bool TryFindHandler_CacheMiss_FirstHandler()
    {
        var inbox = CreateInbox();
        inbox.RegisterHandler(new RouteKeyHandler("order/process"));
        inbox.RegisterHandler(new RoutePrefixHandler("rpc/"));
        inbox.RegisterHandler(new FallbackHandler());
        return inbox.TryFindHandler(_exactMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("CacheMiss")]
    public bool TryFindHandler_CacheMiss_MiddleHandler()
    {
        var inbox = CreateInbox();
        inbox.RegisterHandler(new RouteKeyHandler("order/process"));
        inbox.RegisterHandler(new RoutePrefixHandler("rpc/"));
        inbox.RegisterHandler(new FallbackHandler());
        return inbox.TryFindHandler(_prefixMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("CacheMiss")]
    public bool TryFindHandler_CacheMiss_LastHandler()
    {
        var inbox = CreateInbox();
        inbox.RegisterHandler(new RouteKeyHandler("order/process"));
        inbox.RegisterHandler(new RoutePrefixHandler("rpc/"));
        inbox.RegisterHandler(new FallbackHandler());
        return inbox.TryFindHandler(_noMatchContext, out _);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ScalabilityLinearScan")]
    public bool TryFindHandler_LinearScan_10Handlers_LastMatch()
    {
        return _inboxWith10Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("ScalabilityLinearScan")]
    public bool TryFindHandler_LinearScan_50Handlers_LastMatch()
    {
        return _inboxWith50Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("ScalabilityLinearScan")]
    public bool TryFindHandler_LinearScan_100Handlers_LastMatch()
    {
        return _inboxWith100Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ScalabilityWithCache")]
    public bool TryFindHandler_CacheHit_10Handlers()
    {
        // Warm cache
        _inboxWith10Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
        // Measure cache hit
        return _inboxWith10Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("ScalabilityWithCache")]
    public bool TryFindHandler_CacheHit_50Handlers()
    {
        // Warm cache
        _inboxWith50Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
        // Measure cache hit
        return _inboxWith50Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("ScalabilityWithCache")]
    public bool TryFindHandler_CacheHit_100Handlers()
    {
        // Warm cache
        _inboxWith100Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
        // Measure cache hit
        return _inboxWith100Handlers.TryFindHandler(_lastHandlerMatchContext, out _);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DictionaryVsCache")]
    public bool DictionaryStyle_ExactMatch()
    {
        return _dictionaryStyleInbox.TryFindHandler(_exactMatchContext, out _);
    }

    [Benchmark]
    [BenchmarkCategory("DictionaryVsCache")]
    public bool CapabilityBased_CacheHit_ExactMatch()
    {
        return _inbox.TryFindHandler(_exactMatchContext, out _);
    }

    private static IDurableInbox CreateInbox()
    {
        return new MockDurableInbox();
    }

    private static DurableEnvelope CreateEnvelope(Serializer<TestMessage> serializer, SerializerSessionPool sessionPool, TestMessage message, string routeKey)
    {
        // For benchmarking purposes, we create a minimal envelope with the required properties.
        // The envelope body (Data) is a minimal mock since the benchmark only uses RouteKey for handler dispatch.
        var data = new DurableEnvelopeData(sessionPool);

        return new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = GrainId.Create("test", "sender"),
            ReceiverId = GrainId.Create("test", "recipient"),
            RouteKey = routeKey,
            CorrelationKey = null,
            ReplyTo = null,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class TestMessage
    {
        public string Data { get; set; } = string.Empty;
    }

    private sealed class MockInboxHandlerContext : IInboxHandlerContext
    {
        public MockInboxHandlerContext(DurableEnvelope envelope, GrainId grainId)
        {
            Envelope = envelope;
            GrainId = grainId;
        }

        public DurableEnvelope Envelope { get; }
        public GrainId GrainId { get; }
        public IDurableOutbox Outbox => throw new NotImplementedException();

        public DurableEnvelopeBuilder CreateEnvelope() => throw new NotImplementedException();
        public void Send(DurableEnvelope envelope) => throw new NotImplementedException();
        public void SendError(string errorCode, string message, bool isRetriable = false) => throw new NotImplementedException();
        public void SendError(Exception exception, bool isRetriable = false) => throw new NotImplementedException();
    }

    private sealed class RouteKeyHandler : IInboxHandler
    {
        private readonly string _routeKey;

        public RouteKeyHandler(string routeKey)
        {
            _routeKey = routeKey;
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey == _routeKey;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RoutePrefixHandler : IInboxHandler
    {
        private readonly string _prefix;

        public RoutePrefixHandler(string prefix)
        {
            _prefix = prefix.EndsWith('/') ? prefix : prefix + '/';
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey?.StartsWith(_prefix, StringComparison.Ordinal) == true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FallbackHandler : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context)
        {
            return true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpHandler : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context)
        {
            return true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _dictionary = new();

        public int Count => _dictionary.Count;
        public ICollection<TKey> Keys => _dictionary.Keys;
        public ICollection<TValue> Values => _dictionary.Values;
        public bool IsReadOnly => ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).IsReadOnly;

        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set => _dictionary[key] = value;
        }

        public void Add(TKey key, TValue value) => _dictionary.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Add(item);
        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Contains(item);
        public bool TryGetValue(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value!);
        public bool Remove(TKey key) => _dictionary.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Remove(item);
        public void Clear() => _dictionary.Clear();
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();
    }

    /// <summary>
    /// Mock implementation of IDurableInbox that replicates the handler finding behavior with caching.
    /// Used for benchmarking since DurableInbox is internal.
    /// </summary>
    private sealed class MockDurableInbox : IDurableInbox
    {
        private readonly Dictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inbox = new();
        private readonly Dictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed = new();
        private readonly List<IInboxHandler> _handlers = new();
        private readonly Dictionary<string, IInboxHandler> _legacyRouteHandlers = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IInboxHandler?> _routeCache = new();

        public int Count => _inbox.Count;
        public int Capacity => 1000;
        public IEnumerable<DurableEnvelope> Messages => _inbox.Values;

        public bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
        {
            return _inbox.TryGetValue((senderId, messageId), out envelope);
        }

        public bool RemoveMessage(GrainId senderId, Guid messageId)
        {
            return _inbox.Remove((senderId, messageId));
        }

        public bool ContainsOrProcessed(GrainId senderId, Guid messageId)
        {
            var key = (senderId, messageId);
            return _inbox.ContainsKey(key) || _processed.ContainsKey(key);
        }

        public void MarkProcessed(GrainId senderId, Guid messageId)
        {
            _processed[(senderId, messageId)] = DateTimeOffset.UtcNow;
        }

        public void RegisterHandler(IInboxHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _handlers.Add(handler);
            _routeCache.Clear();
        }

        public bool TryFindHandler(IInboxHandlerContext context, [MaybeNullWhen(false)] out IInboxHandler handler)
        {
            ArgumentNullException.ThrowIfNull(context);

            var routeKey = context.Envelope.RouteKey ?? string.Empty;

            // Try cache first for performance
            if (_routeCache.TryGetValue(routeKey, out var cachedHandler))
            {
                handler = cachedHandler;
                return cachedHandler is not null;
            }

            // Linear scan through handlers in registration order
            foreach (var candidate in _handlers)
            {
                if (candidate.CanHandle(context))
                {
                    handler = candidate;
                    _routeCache[routeKey] = candidate;
                    return true;
                }
            }

            // No handler found - cache the negative result
            handler = null;
            _routeCache[routeKey] = null;
            return false;
        }

        public void RegisterHandler(string routeKey, IInboxHandler handler)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
            ArgumentNullException.ThrowIfNull(handler);

            _legacyRouteHandlers[routeKey] = handler;
            _handlers.Add(new LegacyRouteKeyHandlerWrapper(routeKey, handler));
            _routeCache.Clear();
        }

        public bool HasHandler(string routeKey)
        {
            return _legacyRouteHandlers.ContainsKey(routeKey);
        }

        public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler)
        {
            return _legacyRouteHandlers.TryGetValue(routeKey, out handler);
        }
    }

    /// <summary>
    /// Internal wrapper that adapts legacy route-based handlers to the new CanHandle pattern.
    /// </summary>
    private sealed class LegacyRouteKeyHandlerWrapper : IInboxHandler
    {
        private readonly string _routeKey;
        private readonly IInboxHandler _innerHandler;

        public LegacyRouteKeyHandlerWrapper(string routeKey, IInboxHandler innerHandler)
        {
            _routeKey = routeKey;
            _innerHandler = innerHandler;
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            return context.Envelope.RouteKey == _routeKey;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return _innerHandler.HandleAsync(context, cancellationToken);
        }
    }
}
