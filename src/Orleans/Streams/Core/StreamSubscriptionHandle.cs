using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Handle representing this subsription.
    /// Consumer may serialize and store the handle in order to unsubsribe later, for example
    /// in another activation on this grain.
    /// </summary>
    [Serializable]
    public abstract class StreamSubscriptionHandle<T> : IEquatable<StreamSubscriptionHandle<T>>
    {
        public abstract IStreamIdentity StreamIdentity { get; }

        /// <summary>
        /// Unique identifier for this StreamSubscriptionHandle
        /// </summary>
        public abstract Guid HandleId { get; }

        /// <summary>
        /// Unsubscribe a stream consumer from this observable.
        /// </summary>
        /// <returns>A promise to unsubscription action.
        /// </returns>
        public abstract Task UnsubscribeAsync();

        /// <summary>
        /// Resumed consumption from a subscription to a stream.
        /// </summary>
        /// <param name="observer">The Observer object.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>A promise with an updates subscription handle.
        /// </returns>
        public abstract Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null);

        #region IEquatable<StreamSubscriptionHandle<T>> Members

        public abstract bool Equals(StreamSubscriptionHandle<T> other);

        #endregion
    }
}
