namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerWithMayInterleaveGrain : IGrainWithIntegerKey
{
    Task SetDelay(TimeSpan delay);
    Task GoSlow();
    Task GoFast();
}