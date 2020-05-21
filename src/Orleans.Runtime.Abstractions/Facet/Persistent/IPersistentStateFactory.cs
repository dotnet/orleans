
namespace Orleans.Runtime
{
    public interface IPersistentStateFactory
    {
        IPersistentState<TState> Create<TState>(IGrainContext context, IPersistentStateConfiguration config);
    }
}
