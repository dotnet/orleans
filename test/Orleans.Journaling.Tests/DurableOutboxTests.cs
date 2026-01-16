using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for DurableOutbox storage.
/// </summary>
public class DurableOutboxTests
{
    private static DurableOutbox CreateOutbox(
        IDurableDictionary<Guid, DurableEnvelope>? outbox = null)
    {
        outbox ??= new MockDurableDictionary<Guid, DurableEnvelope>();
        return new DurableOutbox(outbox);
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
        var outboxDict = new MockDurableDictionary<Guid, DurableEnvelope>();

        // Act
        var outbox = new DurableOutbox(outboxDict);

        // Assert
        Assert.NotNull(outbox);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public void Constructor_WithNullOutbox_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DurableOutbox(null!));
    }

    [Fact]
    public void Count_WhenEmpty_ReturnsZero()
    {
        // Arrange
        var outbox = CreateOutbox();

        // Act
        var count = outbox.Count;

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void Send_AddsMessageToOutbox()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope = CreateTestEnvelope();

        // Act
        outbox.Send(envelope);

        // Assert
        Assert.Equal(1, outbox.Count);
    }

    [Fact]
    public void Send_WithMultipleMessages_IncreasesCount()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope1 = CreateTestEnvelope();
        var envelope2 = CreateTestEnvelope();
        var envelope3 = CreateTestEnvelope();

        // Act
        outbox.Send(envelope1);
        outbox.Send(envelope2);
        outbox.Send(envelope3);

        // Assert
        Assert.Equal(3, outbox.Count);
    }

    [Fact]
    public void Send_WithSameMessageId_OverwritesPreviousMessage()
    {
        // Arrange
        var outbox = CreateOutbox();
        var messageId = Guid.NewGuid();
        var envelope1 = CreateTestEnvelope(messageId: messageId, routeKey: "route-1");
        var envelope2 = CreateTestEnvelope(messageId: messageId, routeKey: "route-2");

        // Act
        outbox.Send(envelope1);
        outbox.Send(envelope2);

        // Assert
        Assert.Equal(1, outbox.Count);
        var retrieved = outbox.TryGetMessage(messageId, out var result);
        Assert.True(retrieved);
        Assert.Equal("route-2", result.RouteKey);
    }

    [Fact]
    public void RemoveMessage_WhenMessageExists_RemovesAndReturnsTrue()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope = CreateTestEnvelope();
        outbox.Send(envelope);

        // Act
        var removed = outbox.RemoveMessage(envelope.MessageId);

        // Assert
        Assert.True(removed);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public void RemoveMessage_WhenMessageDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var outbox = CreateOutbox();
        var nonExistentId = Guid.NewGuid();

        // Act
        var removed = outbox.RemoveMessage(nonExistentId);

        // Assert
        Assert.False(removed);
    }

    [Fact]
    public void RemoveMessage_AfterRemoval_MessageNoLongerAccessible()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope = CreateTestEnvelope();
        outbox.Send(envelope);
        outbox.RemoveMessage(envelope.MessageId);

        // Act
        var found = outbox.TryGetMessage(envelope.MessageId, out var result);

        // Assert
        Assert.False(found);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryGetMessage_WhenMessageExists_ReturnsTrueWithEnvelope()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope = CreateTestEnvelope();
        outbox.Send(envelope);

        // Act
        var found = outbox.TryGetMessage(envelope.MessageId, out var result);

        // Assert
        Assert.True(found);
        Assert.NotEqual(default, result);
        Assert.Equal(envelope.MessageId, result.MessageId);
        Assert.Equal(envelope.SenderId, result.SenderId);
        Assert.Equal(envelope.ReceiverId, result.ReceiverId);
        Assert.Equal(envelope.RouteKey, result.RouteKey);
    }

    [Fact]
    public void TryGetMessage_WhenMessageDoesNotExist_ReturnsFalseWithDefault()
    {
        // Arrange
        var outbox = CreateOutbox();
        var nonExistentId = Guid.NewGuid();

        // Act
        var found = outbox.TryGetMessage(nonExistentId, out var result);

        // Assert
        Assert.False(found);
        Assert.Equal(default, result);
    }

    [Fact]
    public void Messages_WhenEmpty_ReturnsEmptyCollection()
    {
        // Arrange
        var outbox = CreateOutbox();

        // Act
        var messages = outbox.Messages.ToList();

        // Assert
        Assert.Empty(messages);
    }

    [Fact]
    public void Messages_WithMultipleMessages_ReturnsAllMessages()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope1 = CreateTestEnvelope();
        var envelope2 = CreateTestEnvelope();
        var envelope3 = CreateTestEnvelope();
        outbox.Send(envelope1);
        outbox.Send(envelope2);
        outbox.Send(envelope3);

        // Act
        var messages = outbox.Messages.ToList();

        // Assert
        Assert.Equal(3, messages.Count);
        Assert.Contains(messages, m => m.MessageId == envelope1.MessageId);
        Assert.Contains(messages, m => m.MessageId == envelope2.MessageId);
        Assert.Contains(messages, m => m.MessageId == envelope3.MessageId);
    }

    [Fact]
    public void Messages_AfterRemoval_DoesNotContainRemovedMessage()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope1 = CreateTestEnvelope();
        var envelope2 = CreateTestEnvelope();
        var envelope3 = CreateTestEnvelope();
        outbox.Send(envelope1);
        outbox.Send(envelope2);
        outbox.Send(envelope3);

        // Act
        outbox.RemoveMessage(envelope2.MessageId);
        var messages = outbox.Messages.ToList();

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.MessageId == envelope1.MessageId);
        Assert.DoesNotContain(messages, m => m.MessageId == envelope2.MessageId);
        Assert.Contains(messages, m => m.MessageId == envelope3.MessageId);
    }

    [Fact]
    public void Integration_SendRetrieveRemove_WorksCorrectly()
    {
        // Arrange
        var outbox = CreateOutbox();
        var senderId = GrainId.Create("sender", "123");
        var receiverId = GrainId.Create("receiver", "456");
        var messageId = Guid.NewGuid();
        var envelope = CreateTestEnvelope(senderId, receiverId, messageId, "payment/process");

        // Act - Send
        outbox.Send(envelope);
        Assert.Equal(1, outbox.Count);

        // Act - Retrieve
        var found = outbox.TryGetMessage(messageId, out var retrieved);
        Assert.True(found);
        Assert.Equal(messageId, retrieved.MessageId);
        Assert.Equal(senderId, retrieved.SenderId);
        Assert.Equal(receiverId, retrieved.ReceiverId);
        Assert.Equal("payment/process", retrieved.RouteKey);

        // Act - Remove
        var removed = outbox.RemoveMessage(messageId);
        Assert.True(removed);
        Assert.Equal(0, outbox.Count);

        // Act - Verify removal
        var foundAfterRemoval = outbox.TryGetMessage(messageId, out _);
        Assert.False(foundAfterRemoval);
    }

    [Fact]
    public void Send_WithDifferentSenders_AllMessagesStored()
    {
        // Arrange
        var outbox = CreateOutbox();
        var sender1 = GrainId.Create("sender", "1");
        var sender2 = GrainId.Create("sender", "2");
        var receiver = GrainId.Create("receiver", "1");
        
        var envelope1 = CreateTestEnvelope(sender1, receiver);
        var envelope2 = CreateTestEnvelope(sender2, receiver);

        // Act
        outbox.Send(envelope1);
        outbox.Send(envelope2);

        // Assert
        Assert.Equal(2, outbox.Count);
        Assert.True(outbox.TryGetMessage(envelope1.MessageId, out var msg1));
        Assert.True(outbox.TryGetMessage(envelope2.MessageId, out var msg2));
        Assert.Equal(sender1, msg1.SenderId);
        Assert.Equal(sender2, msg2.SenderId);
    }

    [Fact]
    public void Send_WithDifferentReceivers_AllMessagesStored()
    {
        // Arrange
        var outbox = CreateOutbox();
        var sender = GrainId.Create("sender", "1");
        var receiver1 = GrainId.Create("receiver", "1");
        var receiver2 = GrainId.Create("receiver", "2");
        
        var envelope1 = CreateTestEnvelope(sender, receiver1);
        var envelope2 = CreateTestEnvelope(sender, receiver2);

        // Act
        outbox.Send(envelope1);
        outbox.Send(envelope2);

        // Assert
        Assert.Equal(2, outbox.Count);
        Assert.True(outbox.TryGetMessage(envelope1.MessageId, out var msg1));
        Assert.True(outbox.TryGetMessage(envelope2.MessageId, out var msg2));
        Assert.Equal(receiver1, msg1.ReceiverId);
        Assert.Equal(receiver2, msg2.ReceiverId);
    }

    [Fact]
    public void Messages_OrderingIsNotGuaranteed()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope1 = CreateTestEnvelope(routeKey: "route-1");
        var envelope2 = CreateTestEnvelope(routeKey: "route-2");
        var envelope3 = CreateTestEnvelope(routeKey: "route-3");
        
        outbox.Send(envelope1);
        outbox.Send(envelope2);
        outbox.Send(envelope3);

        // Act
        var messages = outbox.Messages.ToList();

        // Assert
        // We just verify all messages are present, not their order
        Assert.Equal(3, messages.Count);
        var messageIds = messages.Select(m => m.MessageId).ToHashSet();
        Assert.Contains(envelope1.MessageId, messageIds);
        Assert.Contains(envelope2.MessageId, messageIds);
        Assert.Contains(envelope3.MessageId, messageIds);
    }

    [Fact]
    public void Count_AfterMultipleOperations_IsAccurate()
    {
        // Arrange
        var outbox = CreateOutbox();
        var envelope1 = CreateTestEnvelope();
        var envelope2 = CreateTestEnvelope();
        var envelope3 = CreateTestEnvelope();

        // Act & Assert
        Assert.Equal(0, outbox.Count);
        
        outbox.Send(envelope1);
        Assert.Equal(1, outbox.Count);
        
        outbox.Send(envelope2);
        Assert.Equal(2, outbox.Count);
        
        outbox.Send(envelope3);
        Assert.Equal(3, outbox.Count);
        
        outbox.RemoveMessage(envelope2.MessageId);
        Assert.Equal(2, outbox.Count);
        
        outbox.RemoveMessage(envelope1.MessageId);
        Assert.Equal(1, outbox.Count);
        
        outbox.RemoveMessage(envelope3.MessageId);
        Assert.Equal(0, outbox.Count);
    }
}
