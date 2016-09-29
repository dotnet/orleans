namespace Orleans.Streams
{
    internal interface IInternalStreamProvider : IStreamProviderImpl
    {
        IInternalAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> streamId);
        IInternalAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> streamId);
    }
}
