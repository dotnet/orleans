namespace UnitTests.GrainInterfaces
{
    public interface IFaultableConsumerGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        Task SetFailPeriod(TimeSpan failPeriod);

        Task StopConsuming();

        Task<int> GetNumberConsumed();

        Task<int> GetNumberFailed();

        Task<int> GetErrorCount();
    }
}
