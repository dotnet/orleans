namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerStreamProducerGrain : IGrainWithIntegerKey
    {
        Task Produce(Guid streamId, string providerToUse, string message);
    }
}
