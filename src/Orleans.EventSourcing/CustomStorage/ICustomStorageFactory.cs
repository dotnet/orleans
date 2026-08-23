using Orleans.Runtime;

namespace Orleans.EventSourcing.CustomStorage;

/// <summary>
/// Creates custom storage implementations for grains using the custom storage log-consistency provider.
/// </summary>
/// <remarks>
/// The factory is registered as a keyed singleton and can be called concurrently. Each call returns the storage instance
/// used by one grain activation.
/// </remarks>
public interface ICustomStorageFactory
{
    /// <summary>
    /// Creates the custom storage implementation for a grain.
    /// </summary>
    /// <typeparam name="TState">The grain state type.</typeparam>
    /// <typeparam name="TDelta">The state update type.</typeparam>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The custom storage implementation for the grain activation.</returns>
    ICustomStorageInterface<TState, TDelta> CreateCustomStorage<TState, TDelta>(GrainId grainId);
}
