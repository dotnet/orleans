using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Runtime
{
    internal abstract class TimeboundEntityHolder<T> // where T: ITimebound
    {
        public TimeboundEntityHolder(T entity, DateTime dueTime)
        {
            Entity = entity;
            DueTime = dueTime;
        }

        protected T Entity { get; set; }

        public DateTime DueTime { get; }

        public abstract bool AvailableForDequeue { get; }

        //public abstract bool MustBeReEnqueued { get; }

        public abstract void OnTimeout(Queue<TimeboundEntityHolder<T>> queue); //TimerWheel<T> wheel
    }

    // Removes the need in timer per item, optimized for singlethreaded consumption. 
    internal class TimerWheel<T> : IDisposable where T : ITimebound
    {
        [ThreadStatic] private static  TimerWheelQueueThreadLocals<T> _queueThreadLocals;

        internal static readonly SparseArray<TimerWheelQueueThreadLocals<T>> _allThreadLocals = new SparseArray<TimerWheelQueueThreadLocals<T>>(16);
        private readonly Timer _queueChecker;

        /// <summary>
        /// Initializes the <see cref="T:Orleans.Runtime.TimerWheel"/>.
        /// </summary>
        /// <param name="timerPeriod">Period for the internal timer to check the queue.</param>
        /// <param name="shouldReEnqueue"> If provided will be executed after entry on timeout function.</param>
        /// <param name="shouldDequeue"> If provided will be executed before the check for timeout. 
        /// The latter will not be executed if shouldDequeue func returned true</param>
        public TimerWheel(TimeSpan timerPeriod)
        {
            _queueChecker = new Timer(state =>
            {
                CheckQueues();
            }, null, timerPeriod, timerPeriod);
        }

        public void CheckQueueAndRegister(TimeboundEntityHolder<T> element)
        {
            var tl = EnsureCurrentThreadHasQueue();
            try
            {
                tl.QueueLock.Get();
                CheckQueue(tl.Queue);
                tl.Queue.Enqueue(element);
            }
            finally 
            {
                tl.QueueLock.Release();
            }
        }

        private void CheckQueues()
        {
            foreach (var tl in _allThreadLocals.Current)
            {
                if (tl != null)
                {
                    try
                    {
                        if (!tl.QueueLock.TryGet())
                        {
                            continue;
                        }

                        CheckQueue(tl.Queue);
                    }
                    finally
                    {
                        tl.QueueLock.Release();
                    }
                }
            }
        }


        // Crawls through the callbacks and timeouts expired ones
        private void CheckQueue(Queue<TimeboundEntityHolder<T>> queue)
        {
            var now = DateTime.UtcNow;
            while (true)
            {
                if (queue.Count == 0)
                {
                    return;
                }

                var element = queue.Peek();
                if (element.AvailableForDequeue)
                {
                    queue.Dequeue();
                    continue;
                }

                if (element.DueTime < now)
                {
                    queue.Dequeue();
                    // provided action might be time consuming, so it's safer to perform it outside of the lock

                    element.OnTimeout(queue);
                    //if (element.MustBeReEnqueued)
                    //{
                    //    queue.Enqueue(element);
                    //}
                }
                else
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _queueChecker.Dispose();
        }

        private TimerWheelQueueThreadLocals<T> EnsureCurrentThreadHasQueue()
        {
            if (null == _queueThreadLocals)
            {
                _queueThreadLocals = new TimerWheelQueueThreadLocals<T>();
               _allThreadLocals.Add(_queueThreadLocals);
            }

            return _queueThreadLocals;
        }
    }

    internal sealed class TimerWheelQueueThreadLocals<T> where T: ITimebound
    {
        public readonly InterlockedExchangeLock QueueLock = new InterlockedExchangeLock();
        public readonly Queue<TimeboundEntityHolder<T>> Queue = new Queue<TimeboundEntityHolder<T>>();
       // private static Action<T> RegisterAction = obj => Queue.Enqueue(obj);
       
    }
}
