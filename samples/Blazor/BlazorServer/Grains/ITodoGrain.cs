using BlazorServer.Models;
using Orleans;
using System.Threading.Tasks;

namespace BlazorServer
{
    public interface ITodoGrain : IGrainWithGuidKey
    {
        Task SetAsync(TodoItem item);

        Task ClearAsync();

        Task<TodoItem> GetAsync();
    }
}