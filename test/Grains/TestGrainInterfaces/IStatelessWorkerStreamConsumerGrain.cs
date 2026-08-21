namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerStreamConsumerGrain : IGrainWithIntegerKey
    {
        Task BecomeConsumer(Guid[] streamIds, string providerToUse);

        Task BecomeConsumerFromToken(Guid streamId, string providerToUse);

        Task<int> StopConsuming(Guid streamId, string providerToUse);
    }

    public interface IImplicitStatelessWorkerStreamConsumerGrain : IGrainWithGuidKey
    {
    }

    public interface IUnsupportedStatelessWorkerStreamConsumerGrain : IGrainWithIntegerKey
    {
        Task BecomeConsumer(Guid streamId, string providerToUse);
    }
}
