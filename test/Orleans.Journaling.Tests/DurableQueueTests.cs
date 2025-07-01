using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for DurableQueue, a persistent FIFO queue implementation that uses Orleans' journaling
/// infrastructure to maintain state across grain activations and system restarts.
/// 
/// DurableQueue provides standard queue operations (Enqueue, Dequeue, Peek) while ensuring
/// all operations are journaled for durability. This is particularly useful for implementing
/// reliable message processing patterns in Orleans grains.
/// </summary>
[TestCategory("BVT")]
public class DurableQueueTests : StateMachineTestBase
{
    /// <summary>
    /// Tests basic queue operations: Enqueue, Dequeue, Peek, and Count.
    /// Verifies FIFO ordering and that operations are correctly persisted.
    /// </summary>
    [Fact]
    public async Task DurableQueue_BasicOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("testQueue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Enqueue items
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Enqueue("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(3, queue.Count);
        
        // Act - Peek at the front of the queue
        var peeked = queue.Peek();
        
        // Assert - Peek returns the first item without removing it
        Assert.Equal("one", peeked);
        Assert.Equal(3, queue.Count);
        
        // Act - Dequeue
        var dequeued1 = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("one", dequeued1);
        Assert.Equal(2, queue.Count);
        
        // Act - Dequeue again
        var dequeued2 = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("two", dequeued2);
        Assert.Single(queue);
        
        // Act - Dequeue last item
        var dequeued3 = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("three", dequeued3);
        Assert.Empty(queue);
    }
    
    /// <summary>
    /// Tests that queue state is correctly persisted and recovered.
    /// Creates a queue, adds items, then recreates the queue from the same
    /// storage to verify state recovery.
    /// </summary>
    [Fact]
    public async Task DurableQueue_Persistence_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var codec = CodecProvider.GetCodec<string>();
        var queue1 = new DurableQueue<string>("testQueue", sut.Manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Enqueue items and persist
        queue1.Enqueue("one");
        queue1.Enqueue("two");
        queue1.Enqueue("three");
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        
        // Create a new manager with the same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var queue2 = new DurableQueue<string>("testQueue", sut2.Manager, codec, SessionPool);
        await sut2.Lifecycle.OnStart();

        // Assert - Queue should be recovered
        Assert.Equal(3, queue2.Count);
        Assert.Equal("one", queue2.Peek());
        
        // Act - Dequeue from recovered queue
        var dequeued = queue2.Dequeue();
        await sut2.Manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("one", dequeued);
        Assert.Equal(2, queue2.Count);
    }
    
    /// <summary>
    /// Tests that the queue correctly handles complex value types.
    /// Verifies that custom objects are properly serialized and deserialized
    /// when enqueued and dequeued.
    /// </summary>
    [Fact]
    public async Task DurableQueue_ComplexValues_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<TestPerson>();
        var queue = new DurableQueue<TestPerson>("personQueue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act
        var person1 = new TestPerson { Id = 1, Name = "John", Age = 30 };
        var person2 = new TestPerson { Id = 2, Name = "Jane", Age = 25 };
        
        queue.Enqueue(person1);
        queue.Enqueue(person2);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, queue.Count);
        var peeked = queue.Peek();
        Assert.Equal("John", peeked.Name);
        
        // Act - Dequeue
        var dequeued = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Single(queue);
        Assert.Equal("John", dequeued.Name);
        Assert.Equal(30, dequeued.Age);
    }
    
    /// <summary>
    /// Tests the Clear operation which removes all items from the queue.
    /// Verifies that the clear operation is properly journaled.
    /// </summary>
    [Fact]
    public async Task DurableQueue_Clear_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("clearQueue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add items
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Enqueue("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Clear
        queue.Clear();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Empty(queue);
        Assert.Empty(queue);
    }
    
    /// <summary>
    /// Tests edge cases for operations on an empty queue.
    /// Verifies that Peek and Dequeue throw appropriate exceptions
    /// when the queue is empty.
    /// </summary>
    [Fact]
    public async Task DurableQueue_EmptyQueueOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("emptyQueue", manager, codec, SessionPool);
        await manager.WriteStateAsync(CancellationToken.None);
        await sut.Lifecycle.OnStart();
        
        // Assert
        Assert.Empty(queue);
        
        // Act & Assert - Peek and Dequeue on empty queue should throw
        Assert.Throws<InvalidOperationException>(() => queue.Peek());
        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());
    }
    
    /// <summary>
    /// Tests queue enumeration capabilities.
    /// Verifies that the queue supports standard enumeration patterns
    /// and returns items in FIFO order.
    /// </summary>
    [Fact]
    public async Task DurableQueue_Enumeration_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("enumQueue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add items
        var expectedItems = new List<string> { "one", "two", "three" };
        
        foreach (var item in expectedItems)
        {
            queue.Enqueue(item);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act
        var actualItems = queue.ToList();
        
        // Assert - Items should be in same order as enqueued
        Assert.Equal(expectedItems, actualItems);
    }
    
    /// <summary>
    /// Stress test for queue performance with large numbers of items.
    /// Tests that the journaling system can handle thousands of queue operations
    /// and correctly recover the entire queue state.
    /// </summary>
    [Fact]
    public async Task DurableQueue_LargeNumberOfOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();
        var queue = new DurableQueue<int>("largeQueue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Enqueue many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            queue.Enqueue(i);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(itemCount, queue.Count);
        Assert.Equal(0, queue.Peek());
        
        // Create a new manager with the same storage to test recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var queue2 = new DurableQueue<int>("largeQueue", sut2.Manager, codec, SessionPool);
        await sut2.Lifecycle.OnStart();
        
        // Assert - Large queue is correctly recovered
        Assert.Equal(itemCount, queue2.Count);
        
        // Act - Dequeue all items and verify order
        for (int i = 0; i < itemCount; i++)
        {
            var item = queue2.Dequeue();
            Assert.Equal(i, item);
        }
        
        await sut2.Manager.WriteStateAsync(CancellationToken.None);
        Assert.Empty(queue2);
    }
    
    /// <summary>
    /// Tests complex queue operation patterns with interleaved enqueue/dequeue operations.
    /// Simulates real-world usage where items are added and removed in batches,
    /// verifying that the queue maintains correct ordering and state.
    /// </summary>
    [Fact]
    public async Task DurableQueue_Concurrent_EnqueueDequeue_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();
        var queue = new DurableQueue<int>("concurrentQueue", manager, codec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Simulate a queue with concurrent operations
        const int batchSize = 100;
        
        // First batch: add 100 items
        for (int i = 0; i < batchSize; i++)
        {
            queue.Enqueue(i);
        }
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Remove 50 items
        for (int i = 0; i < batchSize / 2; i++)
        {
            queue.Dequeue();
        }
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Add another 100 items
        for (int i = batchSize; i < batchSize * 2; i++)
        {
            queue.Enqueue(i);
        }
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(batchSize + batchSize / 2, queue.Count); // Should have 150 items
        
        // Create a new manager with the same storage to test recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var queue2 = new DurableQueue<int>("concurrentQueue", sut2.Manager, codec, SessionPool);
        await sut2.Lifecycle.OnStart();
        
        // Assert - Queue should be recovered with correct state and ordering
        Assert.Equal(batchSize + batchSize / 2, queue2.Count);
        
        // First values should be the second half of first batch
        for (int i = batchSize / 2; i < batchSize; i++)
        {
            var item = queue2.Dequeue();
            Assert.Equal(i, item);
        }
        
        // Then we should get the second batch
        for (int i = batchSize; i < batchSize * 2; i++)
        {
            var item = queue2.Dequeue();
            Assert.Equal(i, item);
        }
        
        await sut2.Manager.WriteStateAsync(CancellationToken.None);
        Assert.Empty(queue2);
    }
}
