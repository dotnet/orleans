using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class ConsistentRingQueueBalancer : IGrainRingRangeListener, IStreamQueueBalancer
    {
        private readonly List<IStreamQueueBalanceListener> _queueBalanceListeners = new List<IStreamQueueBalanceListener>();
        private readonly IStreamQueueMapper _streamQueueMapper;
        private IRingRange _myRange;

        public ConsistentRingQueueBalancer(
            IConsistentRingProviderForGrains ringProvider,
            IStreamQueueMapper queueMapper)
        {
            if (ringProvider == null)
            {
                throw new ArgumentNullException("ringProvider");
            }
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }

            _streamQueueMapper = queueMapper;
            _myRange = ringProvider.GetMyRange();

            ringProvider.SubscribeToRangeChangeEvents(this);
        }

        public Task RangeChangeNotification(IRingRange old, IRingRange now)
        {
            _myRange = now;
            var notificatioTasks = new List<Task>(_queueBalanceListeners.Count);
            foreach (IStreamQueueBalanceListener listener in _queueBalanceListeners)
            {
                notificatioTasks.Add(listener.QueueDistributionChangeNotification());
            }
            return Task.WhenAll(notificatioTasks);
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            return _streamQueueMapper.GetQueuesForRange(_myRange);
        }

        public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException("observer");
            }
            lock (_queueBalanceListeners)
            {
                if (_queueBalanceListeners.Contains(observer)) return false;
                
                _queueBalanceListeners.Add(observer);
                return true;
            }
        }

        public bool UnSubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException("observer");
            }
            lock (_queueBalanceListeners)
            {
                return _queueBalanceListeners.Contains(observer) && _queueBalanceListeners.Remove(observer);
            }
        }
    }
}
