namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerSingleActivationGrain : IGrainWithIntegerKey
{
    Task DoWork();
}
