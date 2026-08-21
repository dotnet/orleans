using Orleans.Runtime;

namespace Orleans.EventSourcing.CustomStorage;

/// <summary>
/// Creates custom storage implementations for grains using the custom storage log-consistency provider.
/// </summary>
public interface ICustomStorageFactory
{
    /// <summary>
    /// Creates the custom storage implementation for a grain.
    /// </summary>
    /// <typeparam name="TState">The grain state type.</typeparam>
    /// <typeparam name="TDelta">The state update type.</typeparam>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The custom storage implementation.</returns>
    ICustomStorageInterface<TState, TDelta> CreateCustomStorage<TState, TDelta>(GrainId grainId);
}
