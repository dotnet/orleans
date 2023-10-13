using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Threading;

namespace Orleans.Timers.Internal
{
    /// <summary>
    /// Provides functionality for managing single-shot timers.
    /// </summary>
    public interface ITimerManager
    {
        /// <summary>
        /// Returns a task which will complete when the specified timespan elapses or the provided cancellation token is canceled.
        /// </summary>
        /// <param name="timeSpan">The time span.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> if the timer ran to completion; otherwise <see langword="false"/>.</returns>
        Task<bool> Delay(TimeSpan timeSpan, CancellationToken cancellationToken = default);
    }

    internal class TimerManagerImpl : ITimerManager
    {
        public Task<bool> Delay(TimeSpan timeSpan, CancellationToken cancellationToken = default) => TimerManager.Delay(timeSpan, cancellationToken);
    }

    internal static class TimerManager
    {
        public static Task<bool> Delay(TimeSpan timeSpan, CancellationToken cancellationToken = default) => DelayUntil(DateTime.UtcNow + timeSpan, cancellationToken);

        public static Task<bool> DelayUntil(DateTime dueTime, CancellationToken cancellationToken = default)
        {
            var result = new DelayTimer(dueTime, cancellationToken);
            TimerManager<DelayTimer>.Register(result);
            return result.Completion;
        }

        private sealed class DelayTimer : ITimerCallback, ILinkedListElement<DelayTimer>
        {
            private readonly TaskCompletionSource<bool> completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public DelayTimer(DateTime dueTime, CancellationToken cancellationToken)
            {
                this.DueTime = dueTime;
                this.CancellationToken = cancellationToken;
            }

            public Task<bool> Completion => this.completion.Task;

            public DateTime DueTime { get; }

            public CancellationToken CancellationToken { get; }

            public void OnTimeout() => this.completion.TrySetResult(true);

            public void OnCanceled() => this.completion.TrySetResult(false);

            DelayTimer ILinkedListElement<DelayTimer>.Next { get; set; }
        }
    }

    /// <summary>
    /// Manages timers of a specified type, firing them after they expire.
    /// </summary>
    /// <typeparam name="T">The timer type.</typeparam>
    internal static class TimerManager<T> where T : class, ITimerCallback, ILinkedListElement<T>
    {
        /// <summary>
        /// The maximum number of times a queue can be denied servicing before servicing is mandatory.
        /// </summary>
        private const int MAX_STARVATION = 2;

        /// <summary>
        /// The number of milliseconds between timer servicing ticks.
        /// </summary>
        private const int TIMER_TICK_MILLISECONDS = 50;

        /// <summary>
        /// Lock protecting <see cref="allQueues"/>.
        /// </summary>
        // ReSharper disable once StaticMemberInGenericType
        private static readonly object AllQueuesLock = new object();

#pragma warning disable IDE0052 // Remove unread private members
        private static readonly Timer QueueChecker;
#pragma warning restore IDE0052 // Remove unread private members

        /// <summary>
        /// Collection of all thread-local timer queues.
        /// </summary>
        private static ThreadLocalQueue[] allQueues = new ThreadLocalQueue[16];

        /// <summary>
        /// The queue for the current thread.
        /// </summary>
        [ThreadStatic]
        private static ThreadLocalQueue threadLocalQueue;

        static TimerManager()
        {
            var timerPeriod = TimeSpan.FromMilliseconds(TIMER_TICK_MILLISECONDS);
            QueueChecker = NonCapturingTimer.Create(_ => CheckQueues(), null, timerPeriod, timerPeriod);
        }

        /// <summary>
        /// Registers a timer.
        /// </summary>
        public static void Register(T timer)
        {
            ExpiredTimers expired = null;
            var queue = EnsureCurrentThreadHasQueue();

            try
            {
                queue.Lock.Get();
                queue.AddTail(timer);

                if (queue.StarvationCount >= MAX_STARVATION)
                {
                    // If the queue is too starved, service it now.
                    expired = new ExpiredTimers();
                    CheckQueueInLock(queue, DateTime.UtcNow, expired);
                    Interlocked.Exchange(ref queue.StarvationCount, 0);
                }
            }
            finally
            {
                queue.Lock.TryRelease();

                // Fire expired timers outside of lock.
                expired?.FireTimers();
            }
        }
        
        private static void CheckQueues()
        {
            var expired = new ExpiredTimers();
            var now = DateTime.UtcNow;
            try
            {
                foreach (var queue in allQueues)
                {
                    if (queue == null)
                    {
                        continue;
                    }

                    if (!queue.Lock.TryGet())
                    {
                        // Check for starvation.
                        if (Interlocked.Increment(ref queue.StarvationCount) > MAX_STARVATION)
                        {
                            // If the queue starved, block until the lock can be acquired.
                            queue.Lock.Get();
                            Interlocked.Exchange(ref queue.StarvationCount, 0);
                        }
                        else
                        {
                            // Move on to the next queue.
                            continue;
                        }
                    }

                    try
                    {
                        CheckQueueInLock(queue, now, expired);
                    }
                    finally
                    {
                        queue.Lock.TryRelease();
                    }
                }
            }
            finally
            {
                // Expire timers outside of the loop and outside of any lock.
                expired.FireTimers();
            }
        }

        private static void CheckQueueInLock(ThreadLocalQueue queue, DateTime now, ExpiredTimers expired)
        {
            var previous = default(T);

            for (var current = queue.Head; current != null; current = current.Next)
            {
                if (current.CancellationToken.IsCancellationRequested || current.DueTime < now)
                {
                    // Dequeue and add to expired list for later execution.
                    queue.Remove(previous, current);
                    expired.AddTail(current);
                }
                else
                {
                    // If the current item wasn't removed, update previous.
                    previous = current;
                }
            }
        }

        /// <summary>
        /// Returns the queue for the current thread, creating and registering one if it does not yet exist.
        /// </summary>
        /// <returns>The current thread's <see cref="ThreadLocalQueue"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ThreadLocalQueue EnsureCurrentThreadHasQueue()
        {
            return threadLocalQueue ?? (threadLocalQueue = InitializeThreadLocalQueue());

            ThreadLocalQueue InitializeThreadLocalQueue()
            {
                var threadLocal = new ThreadLocalQueue();
                while (true)
                {
                    lock (AllQueuesLock)
                    {
                        var queues = Volatile.Read(ref allQueues);

                        // Find a spot in the existing array to register this thread.
                        for (var i = 0; i < queues.Length; i++)
                        {
                            if (Volatile.Read(ref queues[i]) == null)
                            {
                                Volatile.Write(ref queues[i], threadLocal);
                                return threadLocal;
                            }
                        }

                        // The existing array is full, so copy all values to a new, larger array and register this thread.
                        var newQueues = new ThreadLocalQueue[queues.Length * 2];
                        Array.Copy(queues, newQueues, queues.Length);
                        newQueues[queues.Length] = threadLocal;
                        Volatile.Write(ref allQueues, newQueues);
                        return threadLocal;
                    }
                }
            }
        }

        /// <summary>
        /// Holds per-thread timer data.
        /// </summary>
        private sealed class ThreadLocalQueue : ILinkedList<T>
        {
            public readonly RecursiveInterlockedExchangeLock Lock = new RecursiveInterlockedExchangeLock();

            /// <summary>
            /// The number of times that this queue has been starved since it was last serviced.
            /// </summary>
            public int StarvationCount;

            public T Head { get; set; }

            public T Tail { get; set; }
        }

        /// <summary>
        /// Holds timers that have expired and should be fired.
        /// </summary>
        private sealed class ExpiredTimers : ILinkedList<T>
        {
            public T Head { get; set; }

            public T Tail { get; set; }

            public void FireTimers()
            {
                var current = this.Head;
                while (current != null)
                {
                    try
                    {
                        if (current.CancellationToken.IsCancellationRequested)
                        {
                            current.OnCanceled();
                        }
                        else
                        {
                            current.OnTimeout();
                        }
                    }
                    catch
                    {
                        // Ignore any exceptions during firing.
                    }

                    current = current.Next;
                }
            }
        }
    }

    internal interface ITimerCallback
    {
        /// <summary>
        /// The UTC time when this timer is due.
        /// </summary>
        DateTime DueTime { get; }

        CancellationToken CancellationToken { get; }

        void OnTimeout();

        void OnCanceled();
    }

    /// <summary>
    /// Represents a linked list.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal interface ILinkedList<T> where T : ILinkedListElement<T>
    {
        /// <summary>
        /// Gets or sets the first element in the list.
        /// This value must never be accessed or modified by user code.
        /// </summary>
        T Head { get; set; }

        /// <summary>
        /// Gets or sets the last element in the list.
        /// This value must never be accessed or modified by user code.
        /// </summary>
        T Tail { get; set; }
    }

    /// <summary>
    /// Represents an element in a linked list.
    /// </summary>
    /// <typeparam name="TSelf">Self-type. The type implementing this interface.</typeparam>
    internal interface ILinkedListElement<TSelf> where TSelf : ILinkedListElement<TSelf>
    {
        /// <summary>
        /// The next element in the list.
        /// This value must never be accessed or modified by user code.
        /// </summary>
        TSelf Next { get; set; }
    }

    internal static class LinkedList
    {
        /// <summary>
        /// Appends an item to the tail of a linked list.
        /// </summary>
        /// <param name="list">The linked list.</param>
        /// <param name="element">The element to append.</param>
        public static void AddTail<TList, TElement>(this TList list, TElement element)
            where TList : class, ILinkedList<TElement> where TElement : class, ILinkedListElement<TElement>
        {
            // If this is the first element, update the head.
            if (list.Head is null) list.Head = element;

            // If this is not the first element, update the current tail.
            var prevTail = list.Tail;
            if (!(prevTail is null)) prevTail.Next = element;

            // Update the tail.
            list.Tail = element;
        }

        /// <summary>
        /// Removes an item from a linked list.
        /// </summary>
        /// <param name="list">The linked list.</param>
        /// <param name="previous">The element before <paramref name="current"/>.</param>
        /// <param name="current">The element to remove.</param>
        public static void Remove<TList, TElement>(this TList list, TElement previous, TElement current)
            where TList : class, ILinkedList<TElement> where TElement : class, ILinkedListElement<TElement>
        {
            var next = current.Next;

            // If not removing the first element, point the previous element at the next element.
            if (!(previous is null)) previous.Next = next;

            // If removing the first element, point the tail at the next element.
            if (ReferenceEquals(list.Head, current))
            {
                list.Head = next ?? previous;
            }

            // If removing the last element, point the tail at the previous element.
            if (ReferenceEquals(list.Tail, current))
            {
                list.Tail = previous;
            }
        }
    }
}