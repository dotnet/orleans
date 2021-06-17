using Orleans;
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BlazorWasm.Grains
{
    public interface ITodoManagerGrain : IGrainWithGuidKey
    {
        Task RegisterAsync(Guid itemKey);
        Task UnregisterAsync(Guid itemKey);

        Task<ImmutableArray<Guid>> GetAllAsync();
    }
}