using Orleans;
using System.Collections.Immutable;

namespace BlazorServer;

public interface ITodoManagerGrain : IGrainWithGuidKey
{
    Task RegisterAsync(Guid itemKey);
    Task UnregisterAsync(Guid itemKey);

    Task<ImmutableArray<Guid>> GetAllAsync();
}
