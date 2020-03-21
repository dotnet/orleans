
namespace Orleans.Runtime
{
    public interface IPersistentStateFactory
    {
        IPersistentState<TState> Create<TState>(IGrainActivationContext context, IPersistentStateConfiguration config);
    }
}
