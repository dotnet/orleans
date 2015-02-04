﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public static class AsyncObservableExtensions
    {
        private static readonly Func<Exception, Task> defaultOnError = ( a ) => { return TaskDone.Done; };
        private static readonly Func<Task> defaultOnCompleted = () => { return TaskDone.Done; };

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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           Func<Task> onCompletedAsync)
        {
            var genericObserver = new GenericAsyncObserver<T>( onNextAsync, onErrorAsync, onCompletedAsync );
            return obs.SubscribeAsync( genericObserver );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync )
        {
            return obs.SubscribeAsync<T>( onNextAsync, onErrorAsync, defaultOnCompleted );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Task> onCompletedAsync )
        {
            return obs.SubscribeAsync<T>( onNextAsync, defaultOnError, onCompletedAsync );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync)
        {
            return obs.SubscribeAsync<T>( onNextAsync, defaultOnError, defaultOnCompleted );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs, 
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync, 
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           Func<Task> onCompletedAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null )
        {
            var genericObserver = new GenericAsyncObserver<T>( onNextAsync, onErrorAsync, onCompletedAsync );
            return obs.SubscribeAsync( genericObserver, token, filterFunc, filterData );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null )
        {
            return obs.SubscribeAsync<T>( onNextAsync, onErrorAsync, defaultOnCompleted, token, filterFunc, filterData );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Task> onCompletedAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null )
        {
            return obs.SubscribeAsync<T>( onNextAsync, defaultOnError, onCompletedAsync, token, filterFunc, filterData );
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
        public static Task<StreamSubscriptionHandle<T>> SubscribeAsync<T>( this IAsyncObservable<T> obs,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           StreamSequenceToken token,
                                                                           StreamFilterPredicate filterFunc = null,
                                                                           object filterData = null )
        {
            return obs.SubscribeAsync<T>(onNextAsync, defaultOnError, defaultOnCompleted, token, filterFunc, filterData);
        }
    }
}
