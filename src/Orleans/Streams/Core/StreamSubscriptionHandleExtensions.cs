using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public static class StreamSubscriptionHandleExtensions
    {
        private static readonly Func<Exception, Task> DefaultOnError = _ => TaskDone.Done;
        private static readonly Func<Task> DefaultOnCompleted = () => TaskDone.Done;

        /// <summary>
        /// Resumes consumption of a stream using delegates.
        /// This method is a helper for the StreamSubscriptionHandle.ResumeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="handle">The subscription handle.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegte that is called for IAsyncObserver.OnErrorAsync.</param>
        /// <param name="onCompletedAsync">Delegte that is called for IAsyncObserver.OnCompletedAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        public static Task<StreamSubscriptionHandle<T>> ResumeAsync<T>(this StreamSubscriptionHandle<T> handle,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           Func<Task> onCompletedAsync,
                                                                           StreamSequenceToken token = null)
        {
            var genericObserver = new GenericAsyncObserver<T>(onNextAsync, onErrorAsync, onCompletedAsync);
            return handle.ResumeAsync(genericObserver, token);
        }

        /// <summary>
        /// Resumes consumption of a stream using delegates.
        /// This method is a helper for the StreamSubscriptionHandle.ResumeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="handle">The subscription handle.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onErrorAsync">Delegte that is called for IAsyncObserver.OnErrorAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        public static Task<StreamSubscriptionHandle<T>> ResumeAsync<T>(this StreamSubscriptionHandle<T> handle,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Exception, Task> onErrorAsync,
                                                                           StreamSequenceToken token = null)
        {
            return handle.ResumeAsync(onNextAsync, onErrorAsync, DefaultOnCompleted, token);
        }

        /// <summary>
        /// Resumes consumption of a stream using delegates.
        /// This method is a helper for the StreamSubscriptionHandle.ResumeAsync allowing the subscribing class to inline the 
        /// handler methods instead of requiring an instance of IAsyncObserver.
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="handle">The subscription handle.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="onCompletedAsync">Delegte that is called for IAsyncObserver.OnCompletedAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        public static Task<StreamSubscriptionHandle<T>> ResumeAsync<T>(this StreamSubscriptionHandle<T> handle,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           Func<Task> onCompletedAsync,
                                                                           StreamSequenceToken token = null)
        {
            return handle.ResumeAsync(onNextAsync, DefaultOnError, onCompletedAsync, token);
        }

        /// <summary>
        /// <exception cref="ArgumentException">Thrown if the supplied stream filter function is not suitable. 
        /// Usually this is because it is not a static method. </exception>
        /// </summary>
        /// <typeparam name="T">The type of object produced by the observable.</typeparam>
        /// <param name="handle">The subscription handle.</param>
        /// <param name="onNextAsync">Delegte that is called for IAsyncObserver.OnNextAsync.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitely unsubscribed.
        /// </returns>
        public static Task<StreamSubscriptionHandle<T>> ResumeAsync<T>(this StreamSubscriptionHandle<T> handle,
                                                                           Func<T, StreamSequenceToken, Task> onNextAsync,
                                                                           StreamSequenceToken token = null)
        {
            return handle.ResumeAsync(onNextAsync, DefaultOnError, DefaultOnCompleted, token);
        }
    }
}
