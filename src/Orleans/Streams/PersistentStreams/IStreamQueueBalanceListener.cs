using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
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