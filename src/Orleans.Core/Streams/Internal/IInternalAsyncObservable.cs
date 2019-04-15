using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    internal interface IInternalAsyncObservable<T> : IAsyncObservable<T>, IAsyncBatchObservable<T>
    {
        Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            StreamSequenceToken token = null);

        Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncBatchObserver<T> observer,
            StreamSequenceToken token = null);

        Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle);

        Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptions();

        Task Cleanup();
    }

        
    internal interface IInternalAsyncBatchObserver<T> : IAsyncBatchProducer<T>
    {
        Task Cleanup();
    }
}
