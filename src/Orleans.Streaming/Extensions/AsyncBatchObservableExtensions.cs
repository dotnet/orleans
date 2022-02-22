using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Extension methods for <see cref="IAsyncBatchObservable{T}"/>.
    /// </summary>
    public static class AsyncBatchObservableExtensions
    {
        private static readonly Func<Exception, Task> DefaultOnError = _ => Task.CompletedTask;
        private static readonly Func<Task> DefaultOnCompleted = () => Task.CompletedTask;

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncBatchObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncBatchObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegate that is called for IAsyncBatchObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegate that is called for IAsyncBatchObserver.OnErrorAsync.</param>
        /// <param name="onCompletedAsync">Delegate that is called for IAsyncBatchObserver.OnCompletedAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncBatchObservable<T> obs,
                                                                           Func<IList<SequentialItem<T>>, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           Func<Task> onCompletedAsync)
        {
            var genericObserver = new GenericAsyncBatchObserver<T>(onNextAsync, onErrorAsync, onCompletedAsync);
            return obs.SubscribeAsync(genericObserver);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncBatchObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncBatchObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegate that is called for IAsyncBatchObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegate that is called for IAsyncBatchObserver.OnErrorAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncBatchObservable<T> obs,
                                                                           Func<IList<SequentialItem<T>>, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync)
        {
            return obs.SubscribeAsync(onNextAsync, onErrorAsync, DefaultOnCompleted);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncBatchObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncBatchObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegate that is called for IAsyncBatchObserver.OnNextAsync.</param>
        /// <param name="onCompletedAsync">Delegate that is called for IAsyncBatchObserver.OnCompletedAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncBatchObservable<T> obs,
                                                                           Func<IList<SequentialItem<T>>, Task> onNextAsync,
                                                                           Func<Task> onCompletedAsync)
        {
            return obs.SubscribeAsync(onNextAsync, DefaultOnError, onCompletedAsync);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncBatchObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncBatchObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegate that is called for IAsyncBatchObserver.OnNextAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncBatchObservable<T> obs,
                                                                           Func<IList<SequentialItem<T>>, Task> onNextAsync)
        {
            return obs.SubscribeAsync(onNextAsync, DefaultOnError, DefaultOnCompleted);
        }
    }
}
