using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Represents a stream item within a sequence.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    public class SequentialItem<T>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SequentialItem{T}"/> class.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="token">The token.</param>
        public SequentialItem(T item, StreamSequenceToken token)
        {
            this.Item = item;
            this.Token = token;
        }

        /// <summary>
        /// Gets the item.
        /// </summary>
        /// <value>The item.</value>
        public T Item { get; }

        /// <summary>
        /// Gets the token.
        /// </summary>
        /// <value>The token.</value>
        public StreamSequenceToken Token { get; }
    }

    /// <summary>
    /// This interface generalizes the IAsyncObserver interface to allow production and consumption of batches of items.
    /// <para>
    /// Note that this interface is implemented by item consumers and invoked (used) by item producers.
    /// This means that the consumer endpoint of a stream implements this interface.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type of object consumed by the observer.</typeparam>
    public interface IAsyncBatchObserver<T>
    {
        /// <summary>
        /// Passes the next batch of items to the consumer.
        /// <para>
        /// The Task returned from this method should be completed when the items' processing has been
        /// sufficiently processed by the consumer to meet any behavioral guarantees.
        /// </para>
        /// <para>
        /// When the consumer is the (producer endpoint of) a stream, the Task is completed when the stream implementation
        /// has accepted responsibility for the items and is assured of meeting its delivery guarantees.
        /// For instance, a stream based on a durable queue would complete the Task when the items have been durably saved.
        /// A stream that provides best-effort at most once delivery would return a Task that is already complete.
        /// </para>
        /// <para>
        /// When the producer is the (consumer endpoint of) a stream, the Task should be completed by the consumer code
        /// when it has accepted responsibility for the items. 
        /// In particular, if the stream provider guarantees at-least-once delivery, then the items should not be considered
        /// delivered until the Task returned by the consumer has been completed.
        /// </para>
        /// </summary>
        /// <param name="items">The item to be passed.</param>
        /// <returns>A Task that is completed when the item has been accepted.</returns>
        Task OnNextAsync(IList<SequentialItem<T>> items);

        /// <summary>
        /// Notifies the consumer that the stream was completed.
        /// <para>
        /// The Task returned from this method should be completed when the consumer is done processing the stream closure.
        /// </para>
        /// </summary>
        /// <returns>A Task that is completed when the stream-complete operation has been accepted.</returns>
        Task OnCompletedAsync();

        /// <summary>
        /// Notifies the consumer that the stream had an error.
        /// <para>
        /// The Task returned from this method should be completed when the consumer is done processing the stream closure.
        /// </para>
        /// </summary>
        /// <param name="ex">An Exception that describes the error that occurred on the stream.</param>
        /// <returns>A Task that is completed when the close has been accepted.</returns>
        Task OnErrorAsync(Exception ex);
    }
}
