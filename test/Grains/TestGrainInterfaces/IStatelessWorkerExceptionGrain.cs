namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerExceptionGrain : IGrainWithIntegerKey
    {
        Task Ping();
    }
}
