namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerWithMayInterleaveGrain : IGrainWithIntegerKey
{
    Task GoSlow(ICallbackGrainObserver callback);
    Task GoFast(ICallbackGrainObserver callback);
}

public interface ICallbackGrainObserver : IGrainObserver
{
    Task WaitAsync();
}