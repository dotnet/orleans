namespace UnitTests.GrainInterfaces
{
    public interface ISampleStreaming_ProducerGrain : IGrainWithGuidKey
    {
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse);

        Task StartPeriodicProducing();

        Task StopPeriodicProducing();

        Task<int> GetNumberProduced(CancellationToken cancellationToken = default);

        Task ClearNumberProduced();
        Task Produce();
    }

    public interface ISampleStreaming_ConsumerGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        Task StopConsuming();

        Task<int> GetNumberConsumed(CancellationToken cancellationToken = default);
    }

    public interface ISampleStreaming_InlineConsumerGrain : ISampleStreaming_ConsumerGrain
    {
    }

    public interface IGrainWithGenericMethodsValue : IGrainWithGuidKey
    {
        ValueTask<int> ValueTaskMethod(bool useCache);
    }
}
