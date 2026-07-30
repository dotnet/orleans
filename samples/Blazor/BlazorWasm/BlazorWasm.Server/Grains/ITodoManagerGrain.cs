using System.Collections.Immutable;

namespace BlazorWasm.Grains;

public interface ITodoManagerGrain : IGrainWithGuidKey
{
    Task RegisterAsync(Guid itemKey);

    Task UnregisterAsync(Guid itemKey);

    Task<ImmutableHashSet<Guid>> GetAllAsync();
}
