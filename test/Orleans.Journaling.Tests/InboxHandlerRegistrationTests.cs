using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for the new handler registration API with CanHandle and TryFindHandler.
/// </summary>
[TestCategory("BVT")]
public class InboxHandlerRegistrationTests
{
    private static DurableInbox CreateInbox(int capacity = 1000)
    {
        var inbox = new MockDurableDictionary<(GrainId, Guid), DurableEnvelope>();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        return new DurableInbox(inbox, processed, capacity);
    }

    private static DurableEnvelope CreateTestEnvelope(string routeKey, HierarchicalKey? correlationKey = null)
    {
        return new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = GrainId.Create("test-sender", Guid.NewGuid().ToString()),
            ReceiverId = GrainId.Create("test-receiver", Guid.NewGuid().ToString()),
            RouteKey = routeKey,
            CorrelationKey = correlationKey,
            Data = new DurableEnvelopeData(null!),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static IInboxHandlerContext CreateContext(DurableEnvelope envelope)
    {
        return new MockInboxHandlerContext(envelope);
    }

    [Fact]
    public void RegisterHandler_WithValidHandler_AddsToList()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestHandler(_ => true);

        // Act
        inbox.RegisterHandler(handler);

        // Assert - verify handler is registered by trying to find it
        var envelope = CreateTestEnvelope("test-route");
        var context = CreateContext(envelope);
        var found = inbox.TryFindHandler(context, out var foundHandler);

        Assert.True(found);
        Assert.Same(handler, foundHandler);
    }

    [Fact]
    public void RegisterHandler_WithNullHandler_ThrowsArgumentNullException()
    {
        // Arrange
        var inbox = CreateInbox();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => inbox.RegisterHandler(null!));
    }

    [Fact]
    public void RegisterHandler_ClearsCacheOnRegistration()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler1 = new TestHandler(ctx => ctx.Envelope.RouteKey == "route1");
        inbox.RegisterHandler(handler1);

        // Prime the cache with a lookup
        var envelope1 = CreateTestEnvelope("route1");
        var context1 = CreateContext(envelope1);
        inbox.TryFindHandler(context1, out _);

        // Act - register a new handler that should clear the cache
        var handler2 = new TestHandler(ctx => ctx.Envelope.RouteKey == "route2");
        inbox.RegisterHandler(handler2);

        // Assert - verify both handlers work after cache clear
        var found1 = inbox.TryFindHandler(context1, out var foundHandler1);
        Assert.True(found1);
        Assert.Same(handler1, foundHandler1);

        var envelope2 = CreateTestEnvelope("route2");
        var context2 = CreateContext(envelope2);
        var found2 = inbox.TryFindHandler(context2, out var foundHandler2);
        Assert.True(found2);
        Assert.Same(handler2, foundHandler2);
    }

    [Fact]
    public void TryFindHandler_WithMatchingHandler_ReturnsTrue()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestHandler(ctx => ctx.Envelope.RouteKey == "payment/process");
        inbox.RegisterHandler(handler);

        var envelope = CreateTestEnvelope("payment/process");
        var context = CreateContext(envelope);

        // Act
        var found = inbox.TryFindHandler(context, out var foundHandler);

        // Assert
        Assert.True(found);
        Assert.Same(handler, foundHandler);
    }

    [Fact]
    public void TryFindHandler_WithNoMatchingHandler_ReturnsFalse()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestHandler(ctx => ctx.Envelope.RouteKey == "payment/process");
        inbox.RegisterHandler(handler);

        var envelope = CreateTestEnvelope("order/create");
        var context = CreateContext(envelope);

        // Act
        var found = inbox.TryFindHandler(context, out var foundHandler);

        // Assert
        Assert.False(found);
        Assert.Null(foundHandler);
    }

    [Fact]
    public void TryFindHandler_WithNullContext_ThrowsArgumentNullException()
    {
        // Arrange
        var inbox = CreateInbox();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => inbox.TryFindHandler(null!, out _));
    }

    [Fact]
    public void TryFindHandler_WithMultipleHandlers_ReturnsFirstMatch()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler1 = new TestHandler(ctx => ctx.Envelope.RouteKey?.StartsWith("api/") == true, name: "Handler1");
        var handler2 = new TestHandler(ctx => ctx.Envelope.RouteKey == "api/users", name: "Handler2");
        var handler3 = new TestHandler(_ => true, name: "Handler3"); // Catch-all

        inbox.RegisterHandler(handler1); // Prefix match
        inbox.RegisterHandler(handler2); // Exact match
        inbox.RegisterHandler(handler3); // Catch-all

        var envelope = CreateTestEnvelope("api/users");
        var context = CreateContext(envelope);

        // Act
        var found = inbox.TryFindHandler(context, out var foundHandler);

        // Assert - should return first matching handler (handler1)
        Assert.True(found);
        Assert.Same(handler1, foundHandler);
    }

    [Fact]
    public void TryFindHandler_CachesResultForSameRouteKey()
    {
        // Arrange
        var inbox = CreateInbox();
        var callCount = 0;
        var handler = new TestHandler(ctx =>
        {
            callCount++;
            return ctx.Envelope.RouteKey == "cached-route";
        });
        inbox.RegisterHandler(handler);

        var envelope = CreateTestEnvelope("cached-route");
        var context = CreateContext(envelope);

        // Act - first call
        inbox.TryFindHandler(context, out _);
        var firstCallCount = callCount;

        // Act - second call with same route key
        inbox.TryFindHandler(context, out _);
        var secondCallCount = callCount;

        // Assert - CanHandle should only be called once due to caching
        Assert.Equal(1, firstCallCount);
        Assert.Equal(1, secondCallCount); // No additional calls
    }

    [Fact]
    public void TryFindHandler_CachesNegativeResults()
    {
        // Arrange
        var inbox = CreateInbox();
        var callCount = 0;
        var handler = new TestHandler(ctx =>
        {
            callCount++;
            return ctx.Envelope.RouteKey == "matching-route";
        });
        inbox.RegisterHandler(handler);

        var envelope = CreateTestEnvelope("non-matching-route");
        var context = CreateContext(envelope);

        // Act - first call
        var found1 = inbox.TryFindHandler(context, out var handler1);

        // Act - second call with same route key
        var found2 = inbox.TryFindHandler(context, out var handler2);

        // Assert
        Assert.False(found1);
        Assert.Null(handler1);
        Assert.False(found2);
        Assert.Null(handler2);
        Assert.Equal(1, callCount); // CanHandle called only once, cached thereafter
    }

    [Fact]
    public void TryFindHandler_WithRouteKeyHandler_FindsExactMatch()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new RouteKeyHandler("order/process");
        inbox.RegisterHandler(handler);

        var envelope = CreateTestEnvelope("order/process");
        var context = CreateContext(envelope);

        // Act
        var found = inbox.TryFindHandler(context, out var foundHandler);

        // Assert
        Assert.True(found);
        Assert.Same(handler, foundHandler);
    }

    [Fact]
    public void TryFindHandler_WithRoutePrefixHandler_FindsPrefixMatch()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new RoutePrefixHandler("rpc/");
        inbox.RegisterHandler(handler);

        var envelope = CreateTestEnvelope("rpc/request");
        var context = CreateContext(envelope);

        // Act
        var found = inbox.TryFindHandler(context, out var foundHandler);

        // Assert
        Assert.True(found);
        Assert.Same(handler, foundHandler);
    }

    [Fact]
    public void TryFindHandler_WithCorrelationHandler_FindsHierarchicalMatch()
    {
        // Arrange
        var inbox = CreateInbox();
        var correlationKey = HierarchicalKey.Create("workflow/parent");
        var handler = new CorrelationHandler(correlationKey);
        inbox.RegisterHandler(handler);

        var childKey = correlationKey.CreateChildKey("child");
        var envelope = CreateTestEnvelope("workflow/event", childKey);
        var context = CreateContext(envelope);

        // Act
        var found = inbox.TryFindHandler(context, out var foundHandler);

        // Assert
        Assert.True(found);
        Assert.Same(handler, foundHandler);
    }

    [Fact]
    public void LegacyRegisterHandler_WithRouteKey_WrapsAndAddsHandler()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestHandler(_ => true);

        // Act - use legacy registration
#pragma warning disable CS0618 // Type or member is obsolete
        inbox.RegisterHandler("legacy-route", handler);
#pragma warning restore CS0618

        // Assert - verify handler is found via TryFindHandler
        var envelope = CreateTestEnvelope("legacy-route");
        var context = CreateContext(envelope);
        var found = inbox.TryFindHandler(context, out var foundHandler);

        Assert.True(found);
        Assert.NotNull(foundHandler);
    }

    [Fact]
    public void LegacyHasHandler_WithRegisteredRoute_ReturnsTrue()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestHandler(_ => true);

#pragma warning disable CS0618
        inbox.RegisterHandler("test-route", handler);

        // Act
        var hasHandler = inbox.HasHandler("test-route");
#pragma warning restore CS0618

        // Assert
        Assert.True(hasHandler);
    }

    [Fact]
    public void LegacyTryGetHandler_WithRegisteredRoute_ReturnsHandler()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestHandler(_ => true);

#pragma warning disable CS0618
        inbox.RegisterHandler("test-route", handler);

        // Act
        var found = inbox.TryGetHandler("test-route", out var foundHandler);
#pragma warning restore CS0618

        // Assert
        Assert.True(found);
        Assert.Same(handler, foundHandler);
    }

    [Fact]
    public void LegacyRegisterHandler_IntegratesWithNewAPI()
    {
        // Arrange
        var inbox = CreateInbox();
        var legacyHandler = new TestHandler(_ => true);
        var newHandler = new TestHandler(ctx => ctx.Envelope.RouteKey == "new-route");

#pragma warning disable CS0618
        inbox.RegisterHandler("legacy-route", legacyHandler);
#pragma warning restore CS0618
        inbox.RegisterHandler(newHandler);

        // Act - find via new API
        var legacyEnvelope = CreateTestEnvelope("legacy-route");
        var legacyContext = CreateContext(legacyEnvelope);
        var foundLegacy = inbox.TryFindHandler(legacyContext, out var foundLegacyHandler);

        var newEnvelope = CreateTestEnvelope("new-route");
        var newContext = CreateContext(newEnvelope);
        var foundNew = inbox.TryFindHandler(newContext, out var foundNewHandler);

        // Assert
        Assert.True(foundLegacy);
        Assert.NotNull(foundLegacyHandler);
        Assert.True(foundNew);
        Assert.Same(newHandler, foundNewHandler);
    }

    // Test helper classes

    private sealed class TestHandler : IInboxHandler
    {
        private readonly Func<IInboxHandlerContext, bool> _canHandlePredicate;
        private readonly string _name;

        public TestHandler(Func<IInboxHandlerContext, bool> canHandlePredicate, string name = "TestHandler")
        {
            _canHandlePredicate = canHandlePredicate;
            _name = name;
        }

        public bool CanHandle(IInboxHandlerContext context) => _canHandlePredicate(context);

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public override string ToString() => _name;
    }

    private sealed class MockInboxHandlerContext : IInboxHandlerContext
    {
        public MockInboxHandlerContext(DurableEnvelope envelope)
        {
            Envelope = envelope;
        }

        public DurableEnvelope Envelope { get; }
        public GrainId GrainId => Envelope.ReceiverId;
        public IDurableOutbox Outbox => throw new NotImplementedException();

        public DurableEnvelopeBuilder CreateEnvelope()
        {
            throw new NotImplementedException();
        }

        public void Send(DurableEnvelope envelope)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class MockDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _inner = new();

        public TValue this[TKey key] { get => _inner[key]; set => _inner[key] = value; }
        public ICollection<TKey> Keys => _inner.Keys;
        public ICollection<TValue> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => _inner.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Add(item);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Contains(item);
        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
        public bool Remove(TKey key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Remove(item);
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _inner.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();
    }

    // Minimal RouteKeyHandler for testing
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

    // Minimal RoutePrefixHandler for testing
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

    // Minimal CorrelationHandler for testing
    private sealed class CorrelationHandler : IInboxHandler
    {
        private readonly HierarchicalKey _correlationKey;

        public CorrelationHandler(HierarchicalKey correlationKey)
        {
            _correlationKey = correlationKey;
        }

        public bool CanHandle(IInboxHandlerContext context)
        {
            var envelopeKey = context.Envelope.CorrelationKey;
            return envelopeKey is not null && _correlationKey.IsAncestorOf(envelopeKey);
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
