namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerGrain : IGrainWithIntegerKey
    {
        Task LongCall();
        Task<Tuple<Guid, string, List<Tuple<DateTime, DateTime>>>> GetCallStats();

        Task DummyCall();
    }
}
