using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Runtime
{
    // Removes the need in timer per item, optimized for singlethreaded consumption. 
    internal class TimerWheel<T> : IDisposable where T : ITimebound
    {
        private const int MaxItemsPerLock = 10;
        private readonly Queue<T> _entries = new Queue<T>();
        private readonly InterlockedExchangeLock _queueLock = new InterlockedExchangeLock();
        private readonly Timer _queueChecker;
        private readonly Func<T, bool> _shouldDequeue;
        private readonly Func<T, bool> _shouldReEnqueue;

        /// <summary>
        /// Initializes the <see cref="T:Orleans.Runtime.TimerWheel"/>.
        /// </summary>
        /// <param name="timerPeriod">Period for the internal timer to check the queue.</param>
        /// <param name="shouldReEnqueue"> If provided will be executed after entry on timeout function.</param>
        /// <param name="shouldDequeue"> If provided will be executed before the check for timeout. 
        /// The latter will not be executed if shouldDequeue func returned true</param>
        public TimerWheel(TimeSpan timerPeriod, Func<T, bool> shouldReEnqueue = null, Func<T, bool> shouldDequeue = null)
        {
            _shouldDequeue = shouldDequeue;
            _shouldReEnqueue = shouldReEnqueue;
            _queueChecker = new Timer(state =>
            {
                CheckQueue();
            }, null, timerPeriod, timerPeriod);
        }

        public void Register(T element)
        {
            try
            {
                _queueLock.Get();
                _entries.Enqueue(element);
            }
            finally
            {
                _queueLock.Release();
            }
        }

        // Crawls through the callbacks and timeouts expired ones
        private void CheckQueue()
        {
            var now = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    _queueLock.Get();
                    for (int i = 0; i < MaxItemsPerLock; i++)
                    {
                        if (_entries.Count == 0)
                        {
                            return;
                        }

                        var element = _entries.Peek();
                        if (_shouldDequeue?.Invoke(element) == true)
                        {
                            _entries.Dequeue();
                            continue;
                        }

                        if (element.DueTime < now)
                        {
                            _entries.Dequeue();

                            // provided action might be time consuming, so it's safer to perform it outside of the lock
                            _queueLock.Release();
                            element.OnTimeout();
                            var shouldReEnqueue = false;
                            if (_shouldReEnqueue != null)
                            {
                                shouldReEnqueue = _shouldReEnqueue(element);
                            }

                            _queueLock.Get();
                            if (shouldReEnqueue)
                            {
                                _entries.Enqueue(element);
                            }
                        }
                        else
                        {
                            return;
                        }
                    }
                }
                finally
                {
                    _queueLock.Release();
                }
            }
        }

        public void Dispose()
        {
            _queueChecker.Dispose();
        }

        internal int QueueCount
        {
            get
            {
                try
                {
                    _queueLock.Get();
                    return _entries.Count;
                }
                finally
                {
                    _queueLock.Release();
                }
                
            }
        }
    }
}
