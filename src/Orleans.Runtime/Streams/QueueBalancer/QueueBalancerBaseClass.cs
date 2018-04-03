using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Base class for StreamQueueBalancer
    /// </summary>
    public abstract class QueueBalancerBase : IStreamQueueBalancer
    {
        /// <summary>
        /// A collection for its IStreamQueueBalancerListener 
        /// </summary>
        protected readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        public QueueBalancerBase()
        {
            this.queueBalanceListeners = new List<IStreamQueueBalanceListener>();
        }
        /// <inheritdoc/>
        public abstract IEnumerable<QueueId> GetMyQueues();
        /// <inheritdoc/>
        public abstract Task Initialize(IStreamQueueMapper queueMapper);
        /// <inheritdoc/>
        public virtual bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            lock (queueBalanceListeners)
            {
                if (queueBalanceListeners.Contains(observer))
                {
                    return false;
                }
                queueBalanceListeners.Add(observer);
                return true;
            }
        }
        /// <inheritdoc/>
        public virtual bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            lock (queueBalanceListeners)
            {
                return queueBalanceListeners.Remove(observer);
            }
        }
    }
}
