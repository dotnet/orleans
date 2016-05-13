using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public static class AsyncObservableExtensions
    {
        private static readonly Func<Exception, Task> DefaultOnError = _ => TaskDone.Done;
        private static readonly Func<Task> DefaultOnCompleted = () => TaskDone.Done;

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegte that is called for IAsyncObserver.OnErrorAsync.</param>
        /// <param name="onCompletedAsync">Delegte that is called for IAsyncObserver.OnCompletedAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           Func<Task> onCompletedAsync)
        {
            var genericObserver = new GenericAsyncObserver<T>(onNextAsync, onErrorAsync, onCompletedAsync);
            return obs.SubscribeAsync(genericObserver);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegte that is called for IAsyncObserver.OnErrorAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync)
        {
            return obs.SubscribeAsync(onNextAsync, onErrorAsync, DefaultOnCompleted);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onCompletedAsync">Delegte that is called for IAsyncObserver.OnCompletedAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Task> onCompletedAsync)
        {
            return obs.SubscribeAsync(onNextAsync, DefaultOnError, onCompletedAsync);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.</returns>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync)
        {
            return obs.SubscribeAsync(onNextAsync, DefaultOnError, DefaultOnCompleted);
        }


        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegte that is called for IAsyncObserver.OnErrorAsync.</param>
        /// <param name="onCompletedAsync">Delegte that is called for IAsyncObserver.OnCompletedAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <param name="filterFunc">Filter to be applied for this subscription</param>
        /// <param name="filterData">Data object that will be passed in to the filterFunc.
        /// This will usually contain any paramaters required by the filterFunc to make it's filtering decision.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if the supplied stream filter function is not suitable. 
        /// Usually this is because it is not a static method. </exception>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           Func<Task> onCompletedAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null)
        {
            var genericObserver = new GenericAsyncObserver<T>(onNextAsync, onErrorAsync, onCompletedAsync);
            return obs.SubscribeAsync(genericObserver, token, filterFunc, filterData);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegte that is called for IAsyncObserver.OnErrorAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <param name="filterFunc">Filter to be applied for this subscription</param>
        /// <param name="filterData">Data object that will be passed in to the filterFunc.
        /// This will usually contain any paramaters required by the filterFunc to make it's filtering decision.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if the supplied stream filter function is not suitable. 
        /// Usually this is because it is not a static method. </exception>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null)
        {
            return obs.SubscribeAsync(onNextAsync, onErrorAsync, DefaultOnCompleted, token, filterFunc, filterData);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onCompletedAsync">Delegte that is called for IAsyncObserver.OnCompletedAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <param name="filterFunc">Filter to be applied for this subscription</param>
        /// <param name="filterData">Data object that will be passed in to the filterFunc.
        /// This will usually contain any paramaters required by the filterFunc to make it's filtering decision.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if the supplied stream filter function is not suitable. 
        /// Usually this is because it is not a static method. </exception>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Task> onCompletedAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null)
        {
            return obs.SubscribeAsync(onNextAsync, DefaultOnError, onCompletedAsync, token, filterFunc, filterData);
        }

        /// <summary>
        /// Subscribe a consumer to this observable using delegates.
        /// This method is a helper for the IAsyncObservable.SubscribeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="obs">The Observable object.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <param name="filterFunc">Filter to be applied for this subscription</param>
        /// <param name="filterData">Data object that will be passed in to the filterFunc.
        /// This will usually contain any paramaters required by the filterFunc to make it's filtering decision.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if the supplied stream filter function is not suitable. 
        /// Usually this is because it is not a static method. </exception>
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>(this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null)
        {
            return obs.SubscribeAsync(onNextAsync, DefaultOnError, DefaultOnCompleted, token, filterFunc, filterData);
        }
    }
}
