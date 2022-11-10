using BlazorWasm.Models;

namespace BlazorWasm.Grains;

public interface ITodoGrain : IGrainWithGuidKey
{
    Task SetAsync(TodoItem item);

    Task ClearAsync();

    Task<TodoItem?> GetAsync();
}
