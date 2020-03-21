using Orleans.Core;

namespace Orleans.Runtime
{
    public interface IPersistentState<TState> : IStorage<TState>
    {
    }
}
