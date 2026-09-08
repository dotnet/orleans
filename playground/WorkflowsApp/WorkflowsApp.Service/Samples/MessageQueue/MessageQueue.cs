// =============================================================================
// MESSAGE QUEUE SAMPLE: Demonstrating IDurableQueue<T>
// =============================================================================
//
// This sample shows how to use IDurableQueue<T> - a persistent FIFO queue
// that survives grain restarts and crashes.
//
// KEY CONCEPTS:
// - IDurableQueue<T> is a first-in-first-out (FIFO) queue
// - Enqueue adds to the back, Dequeue removes from the front
// - All operations are journaled for durability
// - Perfect for: message queues, job queues, event buffers, work items
//
// =============================================================================

using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.MessageQueue;

/// <summary>
/// A durable message queue that persists messages across restarts.
/// </summary>
internal static class MessageQueue
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();

        Console.WriteLine("--- MessageQueue Sample: Demonstrating IDurableQueue<T> ---");
        Console.WriteLine();

        // Get a message queue grain for a specific channel
        var queue = grainFactory.GetGrain<IMessageQueueGrain>("notifications");

        // Clear any existing messages for demo purposes
        await queue.ClearAsync();

        // Enqueue some messages
        await queue.EnqueueAsync(new Message("System", "Server started", Priority.Low));
        await queue.EnqueueAsync(new Message("User:Alice", "Hello world!", Priority.Normal));
        await queue.EnqueueAsync(new Message("System", "High CPU usage detected", Priority.High));
        await queue.EnqueueAsync(new Message("User:Bob", "Meeting reminder", Priority.Normal));

        Console.WriteLine("Enqueued 4 messages.");

        // Check the queue status
        var count = await queue.GetCountAsync();
        Console.WriteLine($"Queue depth: {count}");

        // Peek at the front (doesn't remove)
        var front = await queue.PeekAsync();
        Console.WriteLine($"Next message (peek): [{front!.Priority}] {front.Sender}: {front.Content}");

        Console.WriteLine("\nProcessing messages in FIFO order:");

        // Process all messages
        while (true)
        {
            var message = await queue.DequeueAsync();
            if (message is null)
            {
                Console.WriteLine("  Queue is empty!");
                break;
            }

            Console.WriteLine($"  Processed: [{message.Priority}] {message.Sender}: {message.Content}");
        }

        Console.WriteLine("\nMessageQueue sample completed!");
        Console.WriteLine("Note: Queued messages persist across restarts.\n");
    }

    // -------------------------------------------------------------------------
    // DATA MODEL
    // -------------------------------------------------------------------------

    public enum Priority { Low, Normal, High }

    [GenerateSerializer]
    public record Message(
        [property: Id(0)] string Sender,
        [property: Id(1)] string Content,
        [property: Id(2)] Priority Priority)
    {
        [Id(3)] public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    }

    // -------------------------------------------------------------------------
    // GRAIN INTERFACE
    // -------------------------------------------------------------------------

    [Alias("WorkflowsApp.Service.Samples.MessageQueue.IMessageQueueGrain")]
    public interface IMessageQueueGrain : IGrainWithStringKey
    {
        /// <summary>Adds a message to the end of the queue.</summary>
        [Alias("EnqueueAsync")]
        ValueTask EnqueueAsync(Message message);

        /// <summary>Removes and returns the message at the front of the queue.</summary>
        [Alias("DequeueAsync")]
        ValueTask<Message?> DequeueAsync();

        /// <summary>Returns the message at the front without removing it.</summary>
        [Alias("PeekAsync")]
        ValueTask<Message?> PeekAsync();

        /// <summary>Returns the number of messages in the queue.</summary>
        [Alias("GetCountAsync")]
        ValueTask<int> GetCountAsync();

        /// <summary>Removes all messages from the queue.</summary>
        [Alias("ClearAsync")]
        ValueTask ClearAsync();
    }

    // -------------------------------------------------------------------------
    // GRAIN IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// A grain that maintains a durable message queue using IDurableQueue.
    ///
    /// HOW IT WORKS:
    /// 1. IDurableQueue<Message> is injected via [FromKeyedServices]
    /// 2. The key "messages" identifies this queue in the grain's journal
    /// 3. Enqueue and Dequeue operations are journaled
    /// 4. WriteStateAsync() commits changes to storage
    /// 5. On restart, the queue is reconstructed from the journal
    ///
    /// USE CASES:
    /// - Notification queues
    /// - Job/work item queues
    /// - Event buffers
    /// - Reliable message delivery
    /// </summary>
    internal class MessageQueueGrain(
        [FromKeyedServices("messages")] IDurableQueue<Message> messages)
        : DurableGrain, IMessageQueueGrain
    {
        private readonly IDurableQueue<Message> _messages = messages;

        public async ValueTask EnqueueAsync(Message message)
        {
            // Add to the back of the queue
            _messages.Enqueue(message);
            await WriteStateAsync();
        }

        public async ValueTask<Message?> DequeueAsync()
        {
            // Try to remove from the front of the queue
            if (_messages.TryDequeue(out var message))
            {
                await WriteStateAsync();
                return message;
            }

            return null;
        }

        public ValueTask<Message?> PeekAsync()
        {
            // Look at the front without removing
            if (_messages.TryPeek(out var message))
            {
                return new(message);
            }

            return new((Message?)null);
        }

        public ValueTask<int> GetCountAsync()
        {
            return new(_messages.Count);
        }

        public async ValueTask ClearAsync()
        {
            _messages.Clear();
            await WriteStateAsync();
        }
    }
}
