// =============================================================================
// COUNTER SAMPLE: Demonstrating IDurableValue<T>
// =============================================================================
//
// This sample shows how to use IDurableValue<T> - a persistent single value
// that survives grain restarts and crashes.
//
// KEY CONCEPTS:
// - IDurableValue<T> stores a single value that is automatically persisted
// - Changes are tracked using journaling (append-only log)
// - WriteStateAsync() commits all pending changes to storage
// - Perfect for: counters, settings, flags, single-entity state
//
// =============================================================================

using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.Counter;

/// <summary>
/// A simple durable counter that persists its value across restarts.
/// </summary>
internal static class Counter
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();

        Console.WriteLine("--- Counter Sample: Demonstrating IDurableValue<T> ---");
        Console.WriteLine();

        // Get a counter grain - each counter is identified by a string key
        var counter = grainFactory.GetGrain<ICounterGrain>("my-counter");

        // Show the current value (will be 0 on first run, or retained value after restart)
        var initial = await counter.GetValueAsync();
        Console.WriteLine($"Initial counter value: {initial}");

        // Increment the counter several times
        var value1 = await counter.IncrementAsync();
        Console.WriteLine($"After increment: {value1}");

        var value2 = await counter.IncrementAsync();
        Console.WriteLine($"After increment: {value2}");

        // Add a specific amount
        var value3 = await counter.AddAsync(10);
        Console.WriteLine($"After adding 10: {value3}");

        // Decrement
        var value4 = await counter.DecrementAsync();
        Console.WriteLine($"After decrement: {value4}");

        Console.WriteLine();
        Console.WriteLine("Counter sample completed!");
        Console.WriteLine("Note: The counter value persists across restarts.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // GRAIN INTERFACE
    // -------------------------------------------------------------------------

    [Alias("WorkflowsApp.Service.Samples.Counter.ICounterGrain")]
    public interface ICounterGrain : IGrainWithStringKey
    {
        /// <summary>Gets the current counter value.</summary>
        [Alias("GetValueAsync")]
        ValueTask<long> GetValueAsync();

        /// <summary>Increments the counter by 1 and returns the new value.</summary>
        [Alias("IncrementAsync")]
        ValueTask<long> IncrementAsync();

        /// <summary>Decrements the counter by 1 and returns the new value.</summary>
        [Alias("DecrementAsync")]
        ValueTask<long> DecrementAsync();

        /// <summary>Adds a value to the counter and returns the new value.</summary>
        [Alias("AddAsync")]
        ValueTask<long> AddAsync(long amount);

        /// <summary>Resets the counter to zero.</summary>
        [Alias("ResetAsync")]
        ValueTask ResetAsync();
    }

    // -------------------------------------------------------------------------
    // GRAIN IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// A grain that maintains a durable counter using IDurableValue.
    ///
    /// HOW IT WORKS:
    /// 1. The IDurableValue<long> is injected via [FromKeyedServices]
    /// 2. The key "count" identifies this value in the grain's journal
    /// 3. When you modify .Value, the change is queued for journaling
    /// 4. WriteStateAsync() commits all changes to persistent storage
    /// 5. On grain restart, the value is automatically recovered from the journal
    /// </summary>
    internal class CounterGrain(
        [FromKeyedServices("count")] IDurableValue<long> count)
        : DurableGrain, ICounterGrain
    {
        // The counter value - automatically persisted via journaling
        private readonly IDurableValue<long> _count = count;

        public ValueTask<long> GetValueAsync()
        {
            // Reading the value is instant - no I/O needed
            return new(_count.Value);
        }

        public async ValueTask<long> IncrementAsync()
        {
            // Modify the value - this queues a journal entry
            _count.Value++;

            // Commit the change to storage
            await WriteStateAsync();

            return _count.Value;
        }

        public async ValueTask<long> DecrementAsync()
        {
            _count.Value--;
            await WriteStateAsync();
            return _count.Value;
        }

        public async ValueTask<long> AddAsync(long amount)
        {
            _count.Value += amount;
            await WriteStateAsync();
            return _count.Value;
        }

        public async ValueTask ResetAsync()
        {
            _count.Value = 0;
            await WriteStateAsync();
        }
    }
}
