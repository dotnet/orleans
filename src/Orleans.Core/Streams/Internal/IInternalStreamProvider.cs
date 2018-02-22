namespace Orleans.Streams
{
    internal interface IInternalStreamProvider
    {
        IInternalAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> streamId);
        IInternalAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> streamId);
    }
}
