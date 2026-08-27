namespace UnitTests.GrainInterfaces
{
    public interface ISampleStreaming_ProducerGrain : IGrainWithGuidKey
    {
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse, CancellationToken cancellationToken = default);

        Task StartPeriodicProducing(CancellationToken cancellationToken = default);

        Task StopPeriodicProducing(CancellationToken cancellationToken = default);

        Task<int> GetNumberProduced(CancellationToken cancellationToken = default);

        Task ClearNumberProduced();
        Task Produce(CancellationToken cancellationToken = default);
    }

    public interface ISampleStreaming_ConsumerGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse, CancellationToken cancellationToken = default);

        Task StopConsuming(CancellationToken cancellationToken = default);

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
