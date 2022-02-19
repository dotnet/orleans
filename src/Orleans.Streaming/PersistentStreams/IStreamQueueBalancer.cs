using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// The stream queue balancer is responsible for load balancing queues across all other related queue balancers.  It
    /// notifies any listeners (<code>IStreamQueueBalanceListener</code>) of changes to the distribution of queues.
    /// Method GetMyQueues, SubscribeToQueueDistributionChangeEvents, and UnSubscribeFromQueueDistributionChangeEvents will 
    /// likely be called in the IStreamQueueBalanceListener's thread so they need to be thread safe
    /// </summary>
    public interface IStreamQueueBalancer
    {
        /// <summary>
        /// Initializes this instance.
        /// </summary>
        /// <param name="queueMapper">The queue mapper.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Initialize(IStreamQueueMapper queueMapper);

        /// <summary>
        /// Shutdown the queue balancer.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Shutdown();

        /// <summary>
        /// Retrieves the latest queue distribution for this balancer.
        /// </summary>
        /// <returns>Queue allocated to this balancer.</returns>
        IEnumerable<QueueId> GetMyQueues();

        /// <summary>
        /// Subscribes to receive queue distribution change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive queue distribution change notifications.</param>
        /// <returns>A value indicating whether subscription succeeded or not.</returns>
        bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer);

        /// <summary>
        /// Unsubscribes from receiving queue distribution notifications.
        /// </summary>
        /// <param name="observer">An observer interface to receive queue distribution change notifications.</param>
        /// <returns>A value indicating whether teh unsubscription succeeded or not</returns>
        bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer);
    }

    /// <summary>
    /// The stream queue balancer listener receives notifications from a stream queue balancer (<code>IStreamQueueBalancer</code>)
    /// indicating that the balance of queues has changed.
    /// It should be implemented by components interested in stream queue load balancing.
    /// When change notification is received, listener should request updated list of queues from the queue balancer.
    /// </summary>
    public interface IStreamQueueBalanceListener
    {
        /// <summary>
        /// Called when adapter queue responsibility changes. 
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task QueueDistributionChangeNotification();
    }
}
