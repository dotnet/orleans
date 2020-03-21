using Orleans;
using Sample.Grains.Models;
using System.Threading.Tasks;

namespace Sample.Grains
{
    public interface ITodoGrain : IGrainWithGuidKey
    {
        Task SetAsync(TodoItem item);

        Task ClearAsync();

        Task<TodoItem> GetAsync();
    }
}