using BlazorServer.Models;
using Orleans;

namespace BlazorServer;

public interface ITodoGrain : IGrainWithGuidKey
{
    Task SetAsync(TodoItem item);

    Task ClearAsync();

    Task<TodoItem?> GetAsync();
}
