// =============================================================================
// TODOLIST SAMPLE: Demonstrating IDurableList<T>
// =============================================================================
//
// This sample shows how to use IDurableList<T> - a persistent ordered list
// that survives grain restarts and crashes.
//
// KEY CONCEPTS:
// - IDurableList<T> behaves like a standard List<T>
// - All modifications (Add, Insert, Remove, Set) are journaled
// - Supports indexing, enumeration, and all list operations
// - Perfect for: ordered collections, task lists, history, logs
//
// =============================================================================

using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.TodoList;

/// <summary>
/// A durable to-do list that persists items across restarts.
/// </summary>
internal static class TodoList
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();

        Console.WriteLine("--- TodoList Sample: Demonstrating IDurableList<T> ---");
        Console.WriteLine();

        // Get a to-do list grain for a specific user
        var todoList = grainFactory.GetGrain<ITodoListGrain>("alice");

        // Clear any existing items for demo purposes
        await todoList.ClearAsync();

        // Add some tasks
        await todoList.AddTaskAsync("Buy groceries");
        await todoList.AddTaskAsync("Call mom");
        await todoList.AddTaskAsync("Finish report");

        Console.WriteLine("Added 3 tasks:");
        await PrintTasks(todoList);

        // Complete the first task
        var completed = await todoList.CompleteTaskAsync(0);
        Console.WriteLine($"\nCompleted: {completed}");

        Console.WriteLine("\nRemaining tasks:");
        await PrintTasks(todoList);

        // Insert a high-priority task at the beginning
        await todoList.InsertTaskAtAsync(0, "URGENT: Fix production bug");

        Console.WriteLine("\nAfter adding urgent task:");
        await PrintTasks(todoList);

        // Get task count
        var count = await todoList.GetTaskCountAsync();
        Console.WriteLine($"\nTotal tasks remaining: {count}");

        Console.WriteLine("\nTodoList sample completed!");
        Console.WriteLine("Note: Tasks persist across restarts.\n");
    }

    private static async Task PrintTasks(ITodoListGrain todoList)
    {
        var tasks = await todoList.GetAllTasksAsync();
        for (int i = 0; i < tasks.Count; i++)
        {
            Console.WriteLine($"  [{i}] {tasks[i]}");
        }
    }

    // -------------------------------------------------------------------------
    // DATA MODEL
    // -------------------------------------------------------------------------

    [GenerateSerializer]
    public record TodoItem
    {
        [Id(0)] public required string Title { get; init; }
        [Id(1)] public bool IsCompleted { get; init; }
        [Id(2)] public DateTime CreatedAt { get; init; }
    }

    // -------------------------------------------------------------------------
    // GRAIN INTERFACE
    // -------------------------------------------------------------------------

    [Alias("WorkflowsApp.Service.Samples.TodoList.ITodoListGrain")]
    public interface ITodoListGrain : IGrainWithStringKey
    {
        [Alias("AddTaskAsync")]
        ValueTask AddTaskAsync(string title);

        [Alias("InsertTaskAtAsync")]
        ValueTask InsertTaskAtAsync(int index, string title);

        [Alias("CompleteTaskAsync")]
        ValueTask<string> CompleteTaskAsync(int index);

        [Alias("RemoveTaskAsync")]
        ValueTask<bool> RemoveTaskAsync(int index);

        [Alias("GetAllTasksAsync")]
        ValueTask<List<string>> GetAllTasksAsync();

        [Alias("GetTaskCountAsync")]
        ValueTask<int> GetTaskCountAsync();

        [Alias("ClearAsync")]
        ValueTask ClearAsync();
    }

    // -------------------------------------------------------------------------
    // GRAIN IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// A grain that maintains a durable list of tasks using IDurableList.
    ///
    /// HOW IT WORKS:
    /// 1. IDurableList<string> is injected via [FromKeyedServices]
    /// 2. The key "tasks" identifies this list in the grain's journal
    /// 3. Add, Insert, Remove, Clear, and indexer operations are journaled
    /// 4. WriteStateAsync() commits all pending changes to storage
    /// 5. On restart, the list is automatically reconstructed from the journal
    /// </summary>
    internal class TodoListGrain(
        [FromKeyedServices("tasks")] IDurableList<string> tasks)
        : DurableGrain, ITodoListGrain
    {
        private readonly IDurableList<string> _tasks = tasks;

        public async ValueTask AddTaskAsync(string title)
        {
            // Add appends to the end of the list
            _tasks.Add(title);
            await WriteStateAsync();
        }

        public async ValueTask InsertTaskAtAsync(int index, string title)
        {
            // Insert at a specific position
            _tasks.Insert(index, title);
            await WriteStateAsync();
        }

        public async ValueTask<string> CompleteTaskAsync(int index)
        {
            if (index < 0 || index >= _tasks.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            // Get the task title before removing
            var title = _tasks[index];

            // Remove from the list
            _tasks.RemoveAt(index);
            await WriteStateAsync();

            return title;
        }

        public async ValueTask<bool> RemoveTaskAsync(int index)
        {
            if (index < 0 || index >= _tasks.Count)
            {
                return false;
            }

            _tasks.RemoveAt(index);
            await WriteStateAsync();
            return true;
        }

        public ValueTask<List<string>> GetAllTasksAsync()
        {
            // Convert to a regular list for the response
            return new(_tasks.ToList());
        }

        public ValueTask<int> GetTaskCountAsync()
        {
            return new(_tasks.Count);
        }

        public async ValueTask ClearAsync()
        {
            _tasks.Clear();
            await WriteStateAsync();
        }
    }
}
