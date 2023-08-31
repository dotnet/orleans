namespace UnitTests.GrainInterfaces
{
    public interface ISlowConsumingGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        Task StopConsuming();

        Task<int> GetNumberConsumed();
    }
}
