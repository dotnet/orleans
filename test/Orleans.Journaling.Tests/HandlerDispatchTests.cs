using System.Diagnostics.CodeAnalysis;
using Orleans.DurableMessaging;
using Orleans.Runtime;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class HandlerDispatchTests
{
    [Fact]
    public void TryFindHandler_ReevaluatesHandlerForEveryEnvelope()
    {
        var inbox = CreateDurableInbox();
        var handler = new PredicateHandler(context => context.Envelope.RouteKey == "test/route");
        inbox.RegisterHandler(handler);
        var context = CreateContext("test/route");

        Assert.True(inbox.TryFindHandler(context, out var first));
        Assert.True(inbox.TryFindHandler(context, out var second));

        Assert.Same(handler, first);
        Assert.Same(handler, second);
        Assert.Equal(2, handler.CanHandleCallCount);
    }

    [Fact]
    public void TryFindHandler_SameRouteWithDifferentCorrelationKeys_SelectsMatchingHandler()
    {
        var inbox = CreateDurableInbox();
        var firstKey = HierarchicalKey.Create("workflow/first");
        var secondKey = HierarchicalKey.Create("workflow/second");
        var firstHandler = new PredicateHandler(context => context.Envelope.CorrelationKey == firstKey);
        var secondHandler = new PredicateHandler(context => context.Envelope.CorrelationKey == secondKey);
        inbox.RegisterHandler(firstHandler);
        inbox.RegisterHandler(secondHandler);

        Assert.True(inbox.TryFindHandler(CreateContext("workflow", firstKey), out var first));
        Assert.True(inbox.TryFindHandler(CreateContext("workflow", secondKey), out var second));

        Assert.Same(firstHandler, first);
        Assert.Same(secondHandler, second);
        Assert.Equal(2, firstHandler.CanHandleCallCount);
        Assert.Equal(1, secondHandler.CanHandleCallCount);
    }

    [Fact]
    public void TryFindHandler_PreservesRegistrationOrder()
    {
        var inbox = CreateDurableInbox();
        var firstHandler = new PredicateHandler(_ => true);
        var secondHandler = new PredicateHandler(_ => true);
        inbox.RegisterHandler(firstHandler);
        inbox.RegisterHandler(secondHandler);

        Assert.True(inbox.TryFindHandler(CreateContext("route"), out var handler));

        Assert.Same(firstHandler, handler);
        Assert.Equal(1, firstHandler.CanHandleCallCount);
        Assert.Equal(0, secondHandler.CanHandleCallCount);
    }

    private static DurableInbox CreateDurableInbox()
        => new(
            new InMemoryDurableDictionary<(GrainId, Guid), DurableEnvelope>(),
            new InMemoryDurableDictionary<(GrainId, Guid), DateTimeOffset>(),
            capacity: 1000);

    private static IInboxHandlerContext CreateContext(string routeKey, HierarchicalKey? correlationKey = null)
    {
        var receiverId = GrainId.Create("test-receiver", Guid.NewGuid().ToString("N"));
        return new TestInboxHandlerContext(
            new DurableEnvelope
            {
                MessageId = Guid.NewGuid(),
                SenderId = GrainId.Create("test-sender", Guid.NewGuid().ToString("N")),
                ReceiverId = receiverId,
                RouteKey = routeKey,
                CorrelationKey = correlationKey,
                CreatedAt = DateTimeOffset.UtcNow,
                Data = new DurableEnvelopeData(null!)
            },
            receiverId);
    }

    private sealed class PredicateHandler(Func<IInboxHandlerContext, bool> predicate) : IInboxHandler
    {
        public int CanHandleCallCount { get; private set; }

        public bool CanHandle(IInboxHandlerContext context)
        {
            CanHandleCallCount++;
            return predicate(context);
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class TestInboxHandlerContext(DurableEnvelope envelope, GrainId grainId) : IInboxHandlerContext
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public GrainId GrainId { get; } = grainId;
        public IDurableOutbox Outbox => throw new NotSupportedException();
        public DurableEnvelopeBuilder CreateEnvelope() => throw new NotSupportedException();
        public void Send(DurableEnvelope envelope) => throw new NotSupportedException();
        public void SendError(string errorCode, string message, bool isRetriable = false) => throw new NotSupportedException();
        public void SendError(Exception exception, bool isRetriable = false) => throw new NotSupportedException();
    }

    private sealed class InMemoryDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _storage = [];

        public TValue this[TKey key] { get => _storage[key]; set => _storage[key] = value; }
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
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
