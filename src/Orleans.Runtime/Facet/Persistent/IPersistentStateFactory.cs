
namespace Orleans.Runtime
{
    /// <summary>
    /// Factory for constructing <see cref="IPersistentState{TState}"/> instances for a grain.
    /// </summary>
    public interface IPersistentStateFactory
    {
        /// <summary>
        /// Creates a persistent state instance for the provided grain.
        /// </summary>
        /// <typeparam name="TState">The underlying state type.</typeparam>
        /// <param name="context">The grain context.</param>
        /// <param name="config">The state facet configuration.</param>
        /// <returns>A persistent state instance for the provided grain with the specified configuration.</returns>
        IPersistentState<TState> Create<TState>(IGrainContext context, IPersistentStateConfiguration config);
    }
}
