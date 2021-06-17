using Orleans;
using BlazorWasm.Models;
using System.Threading.Tasks;

namespace BlazorWasm.Grains
{
    public interface ITodoGrain : IGrainWithGuidKey
    {
        Task SetAsync(TodoItem item);

        Task ClearAsync();

        Task<TodoItem> GetAsync();
    }
}