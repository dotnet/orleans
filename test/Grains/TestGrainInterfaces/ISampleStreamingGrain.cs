namespace UnitTests.GrainInterfaces
{
    public interface ISampleStreaming_ProducerGrain : IGrainWithGuidKey
    {
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse, CancellationToken cancellationToken);

        Task StartPeriodicProducing(CancellationToken cancellationToken);

        Task StopPeriodicProducing(CancellationToken cancellationToken);

        Task<int> GetNumberProduced(CancellationToken cancellationToken = default);

        Task ClearNumberProduced();
        Task Produce(CancellationToken cancellationToken);
    }

    public interface ISampleStreaming_ConsumerGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse, CancellationToken cancellationToken);

        Task StopConsuming(CancellationToken cancellationToken);

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
