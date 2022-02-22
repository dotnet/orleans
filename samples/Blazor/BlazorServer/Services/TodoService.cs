using BlazorServer.Models;
using Orleans;
using Orleans.Streams;
using System.Buffers;
using System.Collections.Immutable;

namespace BlazorServer.Services;

public class TodoService
{
    private readonly ILogger<TodoService> _logger;
    private readonly IClusterClient _client;

    public TodoService(ILogger<TodoService> logger, IClusterClient client)
    {
        _logger = logger;
        _client = client;
    }

    public async Task<ImmutableArray<TodoItem>> GetAllAsync(Guid ownerKey)
    {
        // get all the todo item keys for this owner
        var itemKeys = await _client.GetGrain<ITodoManagerGrain>(ownerKey)
            .GetAllAsync();

        // fan out to get the individual items from the cluster in parallel
        var tasks = ArrayPool<Task<TodoItem?>>.Shared.Rent(itemKeys.Length);
        try
        {
            // issue all individual requests at the same time
            for (var i = 0; i < itemKeys.Length; ++i)
            {
                tasks[i] = _client.GetGrain<ITodoGrain>(itemKeys[i]).GetAsync();
            }

            // build the result as requests complete
            var result = ImmutableArray.CreateBuilder<TodoItem>(itemKeys.Length);
            for (var i = 0; i < itemKeys.Length; ++i)
            {
                var item = await tasks[i];

                // we can get a null result if the individual grain failed to unregister
                // in this case we can finish the job here
                if (item is null)
                {
                    await _client.GetGrain<ITodoManagerGrain>(ownerKey)
                        .UnregisterAsync(itemKeys[i]);
                }

                if (item is not null)
                {
                    result.Add(item);
                }
            }
            return result.ToImmutable();
        }
        finally
        {
            ArrayPool<Task<TodoItem?>>.Shared.Return(tasks);
        }
    }

    public Task SetAsync(TodoItem item) =>
        _client.GetGrain<ITodoGrain>(item.Key).SetAsync(item);

    public Task DeleteAsync(Guid itemKey) =>
        _client.GetGrain<ITodoGrain>(itemKey).ClearAsync();

    public Task<StreamSubscriptionHandle<TodoNotification>> SubscribeAsync(
        Guid ownerKey, Func<TodoNotification, Task> action) =>
        _client.GetStreamProvider("SMS")
            .GetStream<TodoNotification>(ownerKey, nameof(ITodoGrain))
            .SubscribeAsync(new TodoItemObserver(_logger, action));

    private class TodoItemObserver : IAsyncObserver<TodoNotification>
    {
        private readonly ILogger<TodoService> _logger;
        private readonly Func<TodoNotification, Task> _onNext;

        public TodoItemObserver(
            ILogger<TodoService> logger,
            Func<TodoNotification, Task> action)
        {
            _logger = logger;
            _onNext = action;
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            return Task.CompletedTask;
        }

        public Task OnNextAsync(
            TodoNotification item,
            StreamSequenceToken? token = null) =>
            _onNext(item);
    }
}
