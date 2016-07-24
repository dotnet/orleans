using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    internal interface IInternalAsyncObservable<T> : IAsyncObservable<T>
    {
        Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            StreamSequenceToken token = null);

        Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle);

        Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptions();

        Task Cleanup();
    }

        
    internal interface IInternalAsyncBatchObserver<in T> : IAsyncBatchObserver<T>
    {
        Task Cleanup();
    }
}
