using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for DurableInbox handler caching behavior.
/// Tests verify that the route cache optimizes handler lookups and invalidates correctly.
/// </summary>
[TestCategory("BVT")]
public class HandlerCacheTests
{
    /// <summary>
    /// Tests that handler lookup results are cached for subsequent lookups with the same route key.
    /// </summary>
    [Fact]
    public void DurableInbox_TryFindHandler_CachesResultAfterFirstLookup()
    {
        // Create inbox with mock dictionaries
        var inbox = CreateDurableInbox();

        // Create a handler that tracks how many times CanHandle is called
        var handler = new CanHandleCountingHandler("test/route");
        inbox.RegisterHandler(handler);

        // Create context
        var context = CreateMockContext(routeKey: "test/route");

        // First lookup - should call CanHandle
        var found1 = inbox.TryFindHandler(context, out var handler1);
        Assert.True(found1);
        Assert.Same(handler, handler1);
        Assert.Equal(1, handler.CanHandleCallCount);

        // Second lookup with same route - should hit cache, not call CanHandle again
        var found2 = inbox.TryFindHandler(context, out var handler2);
        Assert.True(found2);
        Assert.Same(handler, handler2);
        Assert.Equal(1, handler.CanHandleCallCount); // Still 1, not 2

        // Third lookup - verify cache consistency
        var found3 = inbox.TryFindHandler(context, out var handler3);
        Assert.True(found3);
        Assert.Same(handler, handler3);
        Assert.Equal(1, handler.CanHandleCallCount); // Still 1
    }

    /// <summary>
    /// Tests that the cache is cleared when a new handler is registered.
    /// </summary>
    [Fact]
    public void DurableInbox_RegisterHandler_InvalidatesCache()
    {
        var inbox = CreateDurableInbox();

        // Register first handler
        var handler1 = new CanHandleCountingHandler("test/route");
        inbox.RegisterHandler(handler1);

        // Lookup to populate cache
        var context = CreateMockContext(routeKey: "test/route");
        inbox.TryFindHandler(context, out _);
        Assert.Equal(1, handler1.CanHandleCallCount);

        // Second lookup should hit cache
        inbox.TryFindHandler(context, out _);
        Assert.Equal(1, handler1.CanHandleCallCount); // Still 1 (cache hit)

        // Register a new handler - this should clear the cache
        var handler2 = new CanHandleCountingHandler("other/route");
        inbox.RegisterHandler(handler2);

        // Next lookup should call CanHandle again (cache was cleared)
        inbox.TryFindHandler(context, out _);
        Assert.Equal(2, handler1.CanHandleCallCount); // Now 2 (cache miss, re-evaluated)
        // handler2 is not called because handler1 matches first (first-match-wins)
        Assert.Equal(0, handler2.CanHandleCallCount);
    }

    /// <summary>
    /// Tests that cache is invalidated when using legacy RegisterHandler(routeKey, handler) method.
    /// </summary>
    [Fact]
    public void DurableInbox_LegacyRegisterHandler_InvalidatesCache()
    {
        var inbox = CreateDurableInbox();

        // Register handler
        var handler = new CanHandleCountingHandler("test/route");
        inbox.RegisterHandler(handler);

        // Populate cache
        var context = CreateMockContext(routeKey: "test/route");
        inbox.TryFindHandler(context, out _);
        Assert.Equal(1, handler.CanHandleCallCount);

        // Register legacy handler - should clear cache
        var legacyHandler = new TestInboxHandler();
        inbox.RegisterHandler("legacy/route", legacyHandler);

        // Next lookup should miss cache
        inbox.TryFindHandler(context, out _);
        Assert.Equal(2, handler.CanHandleCallCount); // Cache was cleared
    }

    /// <summary>
    /// Tests that negative cache results (no handler found) are cached correctly.
    /// </summary>
    [Fact]
    public void DurableInbox_TryFindHandler_CachesNullResults()
    {
        var inbox = CreateDurableInbox();

        // Register a handler that doesn't match our route
        var handler = new CanHandleCountingHandler("other/route");
        inbox.RegisterHandler(handler);

        // Create context for a route that has no handler
        var context = CreateMockContext(routeKey: "missing/route");

        // First lookup - should scan all handlers
        var found1 = inbox.TryFindHandler(context, out var handler1);
        Assert.False(found1);
        Assert.Null(handler1);
        Assert.Equal(1, handler.CanHandleCallCount); // Called during scan

        // Second lookup - should hit cache (null result)
        var found2 = inbox.TryFindHandler(context, out var handler2);
        Assert.False(found2);
        Assert.Null(handler2);
        Assert.Equal(1, handler.CanHandleCallCount); // Still 1, cache hit

        // Third lookup - verify cache consistency
        var found3 = inbox.TryFindHandler(context, out var handler3);
        Assert.False(found3);
        Assert.Null(handler3);
        Assert.Equal(1, handler.CanHandleCallCount); // Still 1
    }

    /// <summary>
    /// Tests that cached null results are invalidated when a new handler is registered.
    /// </summary>
    [Fact]
    public void DurableInbox_RegisterHandler_InvalidatesCachedNullResults()
    {
        var inbox = CreateDurableInbox();

        // No handlers registered
        var context = CreateMockContext(routeKey: "test/route");

        // First lookup - no handler found, cache null result
        var found1 = inbox.TryFindHandler(context, out var handler1);
        Assert.False(found1);
        Assert.Null(handler1);

        // Second lookup - should hit cached null
        var found2 = inbox.TryFindHandler(context, out var handler2);
        Assert.False(found2);
        Assert.Null(handler2);

        // Register handler that matches the route
        var handler = new CanHandleCountingHandler("test/route");
        inbox.RegisterHandler(handler);

        // Next lookup should find the newly registered handler (cache was cleared)
        var found3 = inbox.TryFindHandler(context, out var handler3);
        Assert.True(found3);
        Assert.Same(handler, handler3);
        Assert.Equal(1, handler.CanHandleCallCount);
    }

    /// <summary>
    /// Tests that the cache handles concurrent access safely.
    /// </summary>
    [Fact]
    public async Task DurableInbox_TryFindHandler_HandlesConcurrentAccess()
    {
        var inbox = CreateDurableInbox();

        // Register multiple handlers
        var handler1 = new CanHandleCountingHandler("route1");
        var handler2 = new CanHandleCountingHandler("route2");
        var handler3 = new CanHandleCountingHandler("route3");
        inbox.RegisterHandler(handler1);
        inbox.RegisterHandler(handler2);
        inbox.RegisterHandler(handler3);

        // Create contexts for each route
        var context1 = CreateMockContext(routeKey: "route1");
        var context2 = CreateMockContext(routeKey: "route2");
        var context3 = CreateMockContext(routeKey: "route3");

        // Perform concurrent lookups
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                inbox.TryFindHandler(context1, out var h);
                Assert.Same(handler1, h);
            }));
            tasks.Add(Task.Run(() =>
            {
                inbox.TryFindHandler(context2, out var h);
                Assert.Same(handler2, h);
            }));
            tasks.Add(Task.Run(() =>
            {
                inbox.TryFindHandler(context3, out var h);
                Assert.Same(handler3, h);
            }));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Each handler should have been called at least once (for initial population)
        // But not 100 times (which would indicate no caching)
        Assert.True(handler1.CanHandleCallCount >= 1);
        Assert.True(handler1.CanHandleCallCount < 100);
        Assert.True(handler2.CanHandleCallCount >= 1);
        Assert.True(handler2.CanHandleCallCount < 100);
        Assert.True(handler3.CanHandleCallCount >= 1);
        Assert.True(handler3.CanHandleCallCount < 100);
    }

    /// <summary>
    /// Tests that different route keys use separate cache entries.
    /// </summary>
    [Fact]
    public void DurableInbox_TryFindHandler_CachesSeparateEntriesPerRoute()
    {
        var inbox = CreateDurableInbox();

        // Register handlers
        var handler1 = new CanHandleCountingHandler("route1");
        var handler2 = new CanHandleCountingHandler("route2");
        inbox.RegisterHandler(handler1);
        inbox.RegisterHandler(handler2);

        // Lookup route1
        var context1 = CreateMockContext(routeKey: "route1");
        inbox.TryFindHandler(context1, out var found1);
        Assert.Same(handler1, found1);
        Assert.Equal(1, handler1.CanHandleCallCount);
        Assert.Equal(0, handler2.CanHandleCallCount); // Not called yet

        // Lookup route2
        var context2 = CreateMockContext(routeKey: "route2");
        inbox.TryFindHandler(context2, out var found2);
        Assert.Same(handler2, found2);
        Assert.Equal(2, handler1.CanHandleCallCount); // Called during linear scan before handler2
        Assert.Equal(1, handler2.CanHandleCallCount); // Now called

        // Lookup route1 again - should hit cache
        inbox.TryFindHandler(context1, out var found1Again);
        Assert.Same(handler1, found1Again);
        Assert.Equal(2, handler1.CanHandleCallCount); // Still 2 (cache hit, no new evaluation)

        // Lookup route2 again - should hit cache
        inbox.TryFindHandler(context2, out var found2Again);
        Assert.Same(handler2, found2Again);
        Assert.Equal(1, handler2.CanHandleCallCount); // Still 1 (cache hit)
    }

    /// <summary>
    /// Tests that null/empty route keys are cached separately.
    /// </summary>
    [Fact]
    public void DurableInbox_TryFindHandler_CachesNullAndEmptyRouteKeys()
    {
        var inbox = CreateDurableInbox();

        // Register handler that accepts null route keys
        var nullHandler = new NullRouteAcceptingHandler();
        inbox.RegisterHandler(nullHandler);

        // Lookup with null route key
        var contextNull = CreateMockContext(routeKey: null);
        var foundNull1 = inbox.TryFindHandler(contextNull, out var handlerNull1);
        Assert.True(foundNull1);
        Assert.Same(nullHandler, handlerNull1);
        Assert.Equal(1, nullHandler.CanHandleCallCount);

        // Second lookup with null - should hit cache
        var foundNull2 = inbox.TryFindHandler(contextNull, out var handlerNull2);
        Assert.True(foundNull2);
        Assert.Same(nullHandler, handlerNull2);
        Assert.Equal(1, nullHandler.CanHandleCallCount); // Cache hit

        // Lookup with empty string route key
        var contextEmpty = CreateMockContext(routeKey: "");
        var foundEmpty1 = inbox.TryFindHandler(contextEmpty, out var handlerEmpty1);
        Assert.True(foundEmpty1);
        Assert.Same(nullHandler, handlerEmpty1);
        // CanHandleCallCount should still be 1 because null and "" map to same cache key (empty string)
        Assert.Equal(1, nullHandler.CanHandleCallCount);
    }

    /// <summary>
    /// Tests cache behavior with handler precedence (first-match-wins).
    /// </summary>
    [Fact]
    public void DurableInbox_TryFindHandler_CachesFirstMatchingHandler()
    {
        var inbox = CreateDurableInbox();

        // Register multiple handlers that could match
        var handler1 = new CanHandleCountingHandler("test/route"); // Exact match
        var handler2 = new PrefixAcceptingHandler("test/"); // Prefix match
        var handler3 = new AcceptAllHandler(); // Catch-all

        inbox.RegisterHandler(handler1);
        inbox.RegisterHandler(handler2);
        inbox.RegisterHandler(handler3);

        var context = CreateMockContext(routeKey: "test/route");

        // First lookup - should find handler1 (first match)
        var found1 = inbox.TryFindHandler(context, out var result1);
        Assert.True(found1);
        Assert.Same(handler1, result1);
        Assert.Equal(1, handler1.CanHandleCallCount);
        Assert.Equal(0, handler2.CanHandleCallCount); // Not reached
        Assert.Equal(0, handler3.CanHandleCallCount); // Not reached

        // Second lookup - should hit cache, not evaluate other handlers
        var found2 = inbox.TryFindHandler(context, out var result2);
        Assert.True(found2);
        Assert.Same(handler1, result2);
        Assert.Equal(1, handler1.CanHandleCallCount); // Cache hit
        Assert.Equal(0, handler2.CanHandleCallCount); // Still not called
        Assert.Equal(0, handler3.CanHandleCallCount); // Still not called
    }

    // Helper Methods

    private static DurableInbox CreateDurableInbox()
    {
        var inboxDict = new InMemoryDurableDictionary<(GrainId, Guid), DurableEnvelope>();
        var processedDict = new InMemoryDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        return new DurableInbox(inboxDict, processedDict, capacity: 1000);
    }

    private static IInboxHandlerContext CreateMockContext(
        string? routeKey = "default-route",
        GrainId? senderId = null,
        GrainId? receiverId = null,
        global::Orleans.HierarchicalKey? correlationKey = null)
    {
        senderId ??= GrainId.Create("test-sender", Guid.NewGuid().ToString("N"));
        receiverId ??= GrainId.Create("test-receiver", Guid.NewGuid().ToString("N"));

        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = senderId.Value,
            ReceiverId = receiverId.Value,
            RouteKey = routeKey!,
            CorrelationKey = correlationKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new DurableEnvelopeData(null!)
        };

        return new MockInboxHandlerContext(envelope, receiverId.Value);
    }

    // Test Handler Implementations

    /// <summary>
    /// Handler that tracks how many times CanHandle is called.
    /// </summary>
    private sealed class CanHandleCountingHandler : IInboxHandler
    {
        private readonly string _routeKey;
        private int _canHandleCallCount;

        public CanHandleCountingHandler(string routeKey)
        {
            _routeKey = routeKey;
        }

        public int CanHandleCallCount => _canHandleCallCount;

        public bool CanHandle(IInboxHandlerContext context)
        {
            Interlocked.Increment(ref _canHandleCallCount);
            return context.Envelope.RouteKey == _routeKey;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Handler that accepts null or empty route keys.
    /// </summary>
    private sealed class NullRouteAcceptingHandler : IInboxHandler
    {
        private int _canHandleCallCount;

        public int CanHandleCallCount => _canHandleCallCount;

        public bool CanHandle(IInboxHandlerContext context)
        {
            Interlocked.Increment(ref _canHandleCallCount);
            return string.IsNullOrEmpty(context.Envelope.RouteKey);
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Handler that accepts any route starting with a prefix.
    /// </summary>
    private sealed class PrefixAcceptingHandler : IInboxHandler
    {
        private readonly string _prefix;
        private int _canHandleCallCount;

        public PrefixAcceptingHandler(string prefix)
        {
            _prefix = prefix;
        }

        public int CanHandleCallCount => _canHandleCallCount;

        public bool CanHandle(IInboxHandlerContext context)
        {
            Interlocked.Increment(ref _canHandleCallCount);
            return context.Envelope.RouteKey?.StartsWith(_prefix, StringComparison.Ordinal) == true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Handler that accepts all messages.
    /// </summary>
    private sealed class AcceptAllHandler : IInboxHandler
    {
        private int _canHandleCallCount;

        public int CanHandleCallCount => _canHandleCallCount;

        public bool CanHandle(IInboxHandlerContext context)
        {
            Interlocked.Increment(ref _canHandleCallCount);
            return true;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Simple test handler.
    /// </summary>
    private sealed class TestInboxHandler : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
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

        public DurableEnvelopeBuilder CreateEnvelope()
        {
            throw new NotImplementedException();
        }

        public void Send(DurableEnvelope envelope)
        {
            throw new NotImplementedException();
        }

        public IDurableOutbox Outbox => throw new NotImplementedException();

        public void SendError(string errorCode, string message, bool isRetriable = false)
        {
            // No-op for testing
        }

        public void SendError(Exception exception, bool isRetriable = false)
        {
            // No-op for testing
        }
    }

    /// <summary>
    /// In-memory implementation of IDurableDictionary for testing.
    /// </summary>
    private sealed class InMemoryDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _storage = new();

        public TValue this[TKey key]
        {
            get => _storage[key];
            set => _storage[key] = value;
        }

        public ICollection<TKey> Keys => _storage.Keys;
        public ICollection<TValue> Values => _storage.Values;
        public int Count => _storage.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => _storage.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_storage).Add(item);
        public void Clear() => _storage.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_storage).Contains(item);
        public bool ContainsKey(TKey key) => _storage.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_storage).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _storage.GetEnumerator();
        public bool Remove(TKey key) => _storage.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_storage).Remove(item);
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _storage.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_storage).GetEnumerator();
    }
}
