namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerStreamConsumerGrain : IGrainWithIntegerKey
    {
        Task BecomeConsumer(Guid streamId, string providerToUse);
    }
}
