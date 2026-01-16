using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for DurableInbox storage and handler management.
/// </summary>
public class DurableInboxTests
{
    private static DurableInbox CreateInbox(
        int capacity = 1000,
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope>? inbox = null,
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset>? processed = null)
    {
        inbox ??= new MockDurableDictionary<(GrainId, Guid), DurableEnvelope>();
        processed ??= new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();

        return new DurableInbox(inbox, processed, capacity);
    }

    /// <summary>
    /// Mock implementation of IDurableDictionary for testing.
    /// </summary>
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

    private static DurableEnvelope CreateTestEnvelope(
        GrainId? senderId = null,
        GrainId? receiverId = null,
        Guid? messageId = null,
        string routeKey = "test-route")
    {
        return new DurableEnvelope
        {
            MessageId = messageId ?? Guid.NewGuid(),
            SenderId = senderId ?? GrainId.Create("test-sender", Guid.NewGuid().ToString()),
            ReceiverId = receiverId ?? GrainId.Create("test-receiver", Guid.NewGuid().ToString()),
            RouteKey = routeKey,
            Data = new DurableEnvelopeData(null!),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange
        var inboxDict = new MockDurableDictionary<(GrainId, Guid), DurableEnvelope>();
        var processedDict = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();

        // Act
        var inbox = new DurableInbox(inboxDict, processedDict, 500);

        // Assert
        Assert.NotNull(inbox);
        Assert.Equal(0, inbox.Count);
        Assert.Equal(500, inbox.Capacity);
    }

    [Fact]
    public void Constructor_WithNullInbox_ThrowsArgumentNullException()
    {
        // Arrange
        var processedDict = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DurableInbox(null!, processedDict, 1000));
    }

    [Fact]
    public void Constructor_WithNullProcessed_ThrowsArgumentNullException()
    {
        // Arrange
        var inboxDict = new MockDurableDictionary<(GrainId, Guid), DurableEnvelope>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DurableInbox(inboxDict, null!, 1000));
    }

    [Fact]
    public void Constructor_WithNegativeCapacity_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var inboxDict = new MockDurableDictionary<(GrainId, Guid), DurableEnvelope>();
        var processedDict = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableInbox(inboxDict, processedDict, -1));
    }

    [Fact]
    public void Constructor_WithZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var inboxDict = new MockDurableDictionary<(GrainId, Guid), DurableEnvelope>();
        var processedDict = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableInbox(inboxDict, processedDict, 0));
    }

    [Fact]
    public void Count_WithEmptyInbox_ReturnsZero()
    {
        // Arrange
        var inbox = CreateInbox();

        // Act & Assert
        Assert.Equal(0, inbox.Count);
    }

    [Fact]
    public void Messages_WithEmptyInbox_ReturnsEmptySequence()
    {
        // Arrange
        var inbox = CreateInbox();

        // Act
        var messages = inbox.Messages;

        // Assert
        Assert.Empty(messages);
    }

    [Fact]
    public void TryGetMessage_WithNonExistentMessage_ReturnsFalse()
    {
        // Arrange
        var inbox = CreateInbox();
        var senderId = GrainId.Create("sender", "1");
        var messageId = Guid.NewGuid();

        // Act
        var result = inbox.TryGetMessage(senderId, messageId, out var envelope);

        // Assert
        Assert.False(result);
        Assert.Equal(default, envelope);
    }

    [Fact]
    public void RemoveMessage_WithNonExistentMessage_ReturnsFalse()
    {
        // Arrange
        var inbox = CreateInbox();
        var senderId = GrainId.Create("sender", "1");
        var messageId = Guid.NewGuid();

        // Act
        var result = inbox.RemoveMessage(senderId, messageId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ContainsOrProcessed_WithNonExistentMessage_ReturnsFalse()
    {
        // Arrange
        var inbox = CreateInbox();
        var senderId = GrainId.Create("sender", "1");
        var messageId = Guid.NewGuid();

        // Act
        var result = inbox.ContainsOrProcessed(senderId, messageId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MarkProcessed_AddsMessageToProcessedDictionary()
    {
        // Arrange
        var inbox = CreateInbox();
        var senderId = GrainId.Create("sender", "1");
        var messageId = Guid.NewGuid();

        // Act
        inbox.MarkProcessed(senderId, messageId);

        // Assert
        Assert.True(inbox.ContainsOrProcessed(senderId, messageId));
    }

    [Fact]
    public void RegisterHandler_WithValidParameters_RegistersHandler()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestInboxHandler();

        // Act
        inbox.RegisterHandler("test-route", handler);

        // Assert
        Assert.True(inbox.HasHandler("test-route"));
    }

    [Fact]
    public void RegisterHandler_WithNullRouteKey_ThrowsArgumentNullException()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestInboxHandler();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => inbox.RegisterHandler(null!, handler));
    }

    [Fact]
    public void RegisterHandler_WithEmptyRouteKey_ThrowsArgumentException()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestInboxHandler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => inbox.RegisterHandler(string.Empty, handler));
    }

    [Fact]
    public void RegisterHandler_WithWhitespaceRouteKey_ThrowsArgumentException()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestInboxHandler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => inbox.RegisterHandler("   ", handler));
    }

    [Fact]
    public void RegisterHandler_WithNullHandler_ThrowsArgumentNullException()
    {
        // Arrange
        var inbox = CreateInbox();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => inbox.RegisterHandler("test-route", null!));
    }

    [Fact]
    public void RegisterHandler_WithDuplicateRoute_ReplacesHandler()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler1 = new TestInboxHandler();
        var handler2 = new TestInboxHandler();

        // Act
        inbox.RegisterHandler("test-route", handler1);
        inbox.RegisterHandler("test-route", handler2);

        // Assert
        Assert.True(inbox.HasHandler("test-route"));
    }

    [Fact]
    public void HasHandler_WithUnregisteredRoute_ReturnsFalse()
    {
        // Arrange
        var inbox = CreateInbox();

        // Act
        var result = inbox.HasHandler("non-existent-route");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasHandler_WithRegisteredRoute_ReturnsTrue()
    {
        // Arrange
        var inbox = CreateInbox();
        var handler = new TestInboxHandler();
        inbox.RegisterHandler("test-route", handler);

        // Act
        var result = inbox.HasHandler("test-route");

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Test inbox handler implementation.
    /// </summary>
    private sealed class TestInboxHandler : IInboxHandler
    {
        public ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
