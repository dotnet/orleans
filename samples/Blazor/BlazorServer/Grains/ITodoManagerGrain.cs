using Orleans;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorServer
{
    public interface ITodoManagerGrain : IGrainWithGuidKey
    {
        Task RegisterAsync(Guid itemKey);
        Task UnregisterAsync(Guid itemKey);

        Task<ImmutableArray<Guid>> GetAllAsync();
    }
}