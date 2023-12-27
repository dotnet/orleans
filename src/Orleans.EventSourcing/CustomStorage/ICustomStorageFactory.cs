using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime;

namespace OrleansEventSourcing.CustomStorage;

public interface ICustomStorageFactory
{
    public ICustomStorageInterface<TState, TDelta> CreateCustomStorage<TState, TDelta>(GrainId grainId)
        where TState : class, new()
        where TDelta : class;
}