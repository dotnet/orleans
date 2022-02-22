using Orleans.Core;

namespace Orleans.Runtime
{
    /// <summary>
    /// Provides access to grain state with functionality to save, clear, and refresh the state.
    /// </summary>
    /// <typeparam name="TState">The underlying state type.</typeparam>
    /// <seealso cref="Orleans.Core.IStorage{TState}" />
    public interface IPersistentState<TState> : IStorage<TState>
    {
    }
}
