using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// This interface generalizes the IAsyncObserver interface to allow production and consumption of batches of items.
    /// <para>
    /// Note that this interface is implemented by item consumers and invoked (used) by item producers.
    /// This means that the consumer endpoint of a stream implements this interface.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type of object consumed by the observer.</typeparam>
    public interface IAsyncBatchObserver<in T> : IAsyncObserver<T>
    {
        /// <summary>
        /// Passes the next batch of items to the consumer.
        /// <para>
        /// The Task returned from this method should be completed when all items in the batch have been
        /// sufficiently processed by the consumer to meet any behavioral guarantees.
        /// </para>
        /// <para>
        /// That is, the semantics of the returned Task is the same as for <code>OnNextAsync</code>,
        /// extended for all items in the batch.
        /// </para>
        /// </summary>
        /// <param name="batch">The items to be passed.</param>
        /// <param name="token">The stream sequence token of this batch of items.</param>
        /// <returns>A Task that is completed when the batch has been accepted.</returns>
        Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token = null);
    }
}