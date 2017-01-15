using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// The stream queue balancer is responsible for load balancing queues across all other related queue balancers.  It
    /// notifies any listeners (<code>IStreamQueueBalanceListener</code>) of changes to the distribution of queues.
    /// </summary>
    internal interface IStreamQueueBalancer
    {
        /// <summary>
        /// Retrieves the latest queue distribution for this balancer.
        /// </summary>
        /// <returns>Queue allocated to this balancer.</returns>
        IEnumerable<QueueId> GetMyQueues();

        /// <summary>
        /// Subscribe to receive queue distribution change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive queue distribution change notifications.</param>
        /// <returns>Bool value indicating that subscription succeeded or not.</returns>
        bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer);

        /// <summary>
        /// Unsubscribe from receiving queue distribution notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive queue distribution change notifications.</param>
        /// <returns>Bool value indicating that unsubscription succeeded or not</returns>
        bool UnSubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer);
    }

    /// <summary>
    /// The stream queue balancer listener receives notifications from a stream queue balancer (<code>IStreamQueueBalancer</code>)
    /// indicating that the balance of queues has changed.
    /// It should be implemented by components interested in stream queue load balancing.
    /// When change notification is received, listener should request updated list of queues from the queue balancer.
    /// </summary>
    internal interface IStreamQueueBalanceListener : IAddressable
    {
        /// <summary>
        /// Receive notifications about adapter queue responsibility changes. 
        /// </summary>
        /// <returns></returns>
        Task QueueDistributionChangeNotification();
    }
}
