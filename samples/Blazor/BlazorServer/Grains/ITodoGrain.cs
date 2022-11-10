using BlazorServer.Models;

namespace BlazorServer;

public interface ITodoGrain : IGrainWithGuidKey
{
    Task SetAsync(TodoItem item);

    Task ClearAsync();

    Task<TodoItem?> GetAsync();
}
