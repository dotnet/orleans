using System.Collections.Immutable;
using BlazorServer.Models;
using Orleans.Streams;

namespace BlazorServer.Services;

public sealed class TodoService(ILogger<TodoService> logger, IClusterClient client)
{
    public async Task<ImmutableArray<TodoItem>> GetAllAsync(Guid ownerKey)
    {
        // get all the todo item keys for this owner
        var itemKeys = await client
            .GetGrain<ITodoManagerGrain>(ownerKey)
            .GetAllAsync();

        // fan out to get the individual items from the cluster in parallel
        // issue all individual requests at the same time
        var tasks = itemKeys
            .Select(async itemId =>
            {
                var item = await client
                    .GetGrain<ITodoGrain>(itemId)
                    .GetAsync();

                // we can get a null result if the individual grain failed to unregister
                // in this case we can finish the job here
                if (item is null)
                {
                    await client
                        .GetGrain<ITodoManagerGrain>(ownerKey)
                        .UnregisterAsync(itemId);
                }

                return item;
            });

        var result = await Task.WhenAll(tasks);

        // filter out null TodoItems and return as immutable array.
        return result
            .OfType<TodoItem>()
            .ToImmutableArray();
    }

    public Task SetAsync(TodoItem item) =>
        client.GetGrain<ITodoGrain>(item.Key).SetAsync(item);

    public Task DeleteAsync(Guid itemKey) =>
        client.GetGrain<ITodoGrain>(itemKey).ClearAsync();

    public Task<StreamSubscriptionHandle<TodoNotification>> SubscribeAsync(
        Guid ownerKey, Func<TodoNotification, Task> action) =>
        client.GetStreamProvider("MemoryStreams")
            .GetStream<TodoNotification>(ownerKey)
            .SubscribeAsync(new TodoItemObserver(logger, action));
}

sealed file class TodoItemObserver(
    ILogger<TodoService> logger,
    Func<TodoNotification, Task> action) : IAsyncObserver<TodoNotification>
{
    public Task OnCompletedAsync() => Task.CompletedTask;

    public Task OnErrorAsync(Exception ex)
    {
        logger.LogError(ex, ex.Message);
        return Task.CompletedTask;
    }

    public Task OnNextAsync(
        TodoNotification item,
        StreamSequenceToken? token = null) =>
        action(item);
}
