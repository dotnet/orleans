//-----------------------------------------------------------------------
// <copyright file="Helios.Concurrency.DedicatedThreadPool.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

/*
 * Copyright 2015 Roger Alsing, Aaron Stannard
 * Helios.DedicatedThreadPool - https://github.com/helios-io/DedicatedThreadPool
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// The type of threads to use - either foreground or background threads.
    /// </summary>
    public enum ThreadType
    {
        Foreground,
        Background
    }

    /// <summary>
    /// Provides settings for a dedicated thread pool
    /// </summary>
    public class DedicatedThreadPoolSettings
    {
        /// <summary>
        /// Background threads are the default thread type
        /// </summary>
        public const ThreadType DefaultThreadType = ThreadType.Background;

        public DedicatedThreadPoolSettings(int numThreads,
                                           string name = null,
                                           TimeSpan? deadlockTimeout = null,
                                           ApartmentState apartmentState = ApartmentState.Unknown,
                                           Action<Exception> exceptionHandler = null,
                                           int threadMaxStackSize = 0)
            : this(numThreads, DefaultThreadType, name, deadlockTimeout, apartmentState, exceptionHandler, threadMaxStackSize)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="DedicatedThreadPoolSettings"/> class.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// This exception is thrown if the given <paramref name="deadlockTimeout"/> is set to less than 1ms.
        /// It can also be thrown if the given <paramref name="numThreads"/> is set to less than one.
        /// </exception>
        public DedicatedThreadPoolSettings(int numThreads,
                                           ThreadType threadType,
                                           string name = null,
                                           TimeSpan? deadlockTimeout = null,
                                           ApartmentState apartmentState = ApartmentState.Unknown,
                                           Action<Exception> exceptionHandler = null,
                                           int threadMaxStackSize = 0)
        {
            Name = name ?? ("DedicatedThreadPool-" + Guid.NewGuid());
            ThreadType = threadType;
            NumThreads = numThreads;
            DeadlockTimeout = deadlockTimeout;
            ApartmentState = apartmentState;
            ExceptionHandler = exceptionHandler ?? (ex => { });
            ThreadMaxStackSize = threadMaxStackSize;

            if (deadlockTimeout.HasValue && deadlockTimeout.Value.TotalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(deadlockTimeout), $"deadlockTimeout must be null or at least 1ms. Was {deadlockTimeout}.");
            if (numThreads <= 0)
                throw new ArgumentOutOfRangeException(nameof(numThreads), $"numThreads must be at least 1. Was {numThreads}");
        }

        /// <summary>
        /// The total number of threads to run in this thread pool.
        /// </summary>
        public int NumThreads { get; private set; }

        /// <summary>
        /// The type of threads to run in this thread pool.
        /// </summary>
        public ThreadType ThreadType { get; private set; }

        /// <summary>
        /// Apartment state for threads to run in this thread pool
        /// </summary>
        public ApartmentState ApartmentState { get; private set; }

        /// <summary>
        /// Interval to check for thread deadlocks.
        /// 
        /// If a thread takes longer than <see cref="DeadlockTimeout"/> it will be aborted
        /// and replaced.
        /// </summary>
        public TimeSpan? DeadlockTimeout { get; private set; }

        public string Name { get; private set; }

        public Action<Exception> ExceptionHandler { get; private set; }

        /// <summary>
        /// Gets the thread stack size, 0 represents the default stack size.
        /// </summary>
        public int ThreadMaxStackSize { get; private set; }
    }

    /// <summary>
    /// TaskScheduler for working with a <see cref="DedicatedThreadPool"/> instance
    /// </summary>
    public class DedicatedThreadPoolTaskScheduler : TaskScheduler
    {
        private static readonly Lazy<DedicatedThreadPoolTaskScheduler> _instance =
            new Lazy<DedicatedThreadPoolTaskScheduler>(() => new DedicatedThreadPoolTaskScheduler(new DedicatedThreadPool(new DedicatedThreadPoolSettings(Environment.ProcessorCount))));

        public static readonly DedicatedThreadPoolTaskScheduler Instance = _instance.Value;

        // Indicates whether the current thread is processing work items.
        [ThreadStatic]
        private static bool _currentThreadIsRunningTasks;

        /// <summary>
        /// Number of tasks currently running
        /// </summary>
        private volatile int _parallelWorkers = 0;

        private readonly LinkedList<Task> _tasks = new LinkedList<Task>();

        private readonly DedicatedThreadPool _pool;

        public DedicatedThreadPool Pool => _pool;

        public DedicatedThreadPoolTaskScheduler(DedicatedThreadPool pool)
        {
            _pool = pool;
        }

        protected override void QueueTask(Task task)
        {
            lock (_tasks)
            {
                _tasks.AddLast(task);
            }

            EnsureWorkerRequested();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            //current thread isn't running any tasks, can't execute inline
            if (!_currentThreadIsRunningTasks) return false;

            //remove the task from the queue if it was previously added
            if (taskWasPreviouslyQueued)
                if (TryDequeue(task))
                    return TryExecuteTask(task);
                else
                    return false;
            return TryExecuteTask(task);
        }

        protected override bool TryDequeue(Task task)
        {
            lock (_tasks) return _tasks.Remove(task);
        }

        /// <summary>
        /// Level of concurrency is directly equal to the number of threads
        /// in the <see cref="DedicatedThreadPool"/>.
        /// </summary>
        public override int MaximumConcurrencyLevel
        {
            get { return _pool.Settings.NumThreads; }
        }

        /// <summary></summary>
        /// <exception cref="NotSupportedException">
        /// This exception is thrown if can't ensure a thread-safe return of the list of tasks.
        /// </exception>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(_tasks, ref lockTaken);

                //should this be immutable?
                if (lockTaken) return _tasks;
                else throw new NotSupportedException();
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_tasks);
            }
        }

        private void EnsureWorkerRequested()
        {
            var count = _parallelWorkers;
            while (count < _pool.Settings.NumThreads)
            {
                var prev = Interlocked.CompareExchange(ref _parallelWorkers, count + 1, count);
                if (prev == count)
                {
                    RequestWorker();
                    break;
                }
                count = prev;
            }
        }

        private void ReleaseWorker()
        {
            var count = _parallelWorkers;
            while (count > 0)
            {
                var prev = Interlocked.CompareExchange(ref _parallelWorkers, count - 1, count);
                if (prev == count)
                {
                    break;
                }
                count = prev;
            }
        }

        private void RequestWorker()
        {
            _pool.QueueUserWorkItem(() =>
            {
                // this thread is now available for inlining
                _currentThreadIsRunningTasks = true;
                try
                {
                    // Process all available items in the queue. 
                    while (true)
                    {
                        Task item;
                        lock (_tasks)
                        {
                            // done processing
                            if (_tasks.Count == 0)
                            {
                                ReleaseWorker();
                                break;
                            }

                            // Get the next item from the queue
                            item = _tasks.First.Value;
                            _tasks.RemoveFirst();
                        }

                        // Execute the task we pulled out of the queue 
                        TryExecuteTask(item);
                    }
                }
                // We're done processing items on the current thread 
                finally { _currentThreadIsRunningTasks = false; }
            });
        }
    }



    /// <summary>
    /// An instanced, dedicated thread pool.
    /// </summary>
    public sealed class DedicatedThreadPool : IDisposable
    {
        public DedicatedThreadPool(DedicatedThreadPoolSettings settings)
        {
            _workQueue = new ThreadPoolWorkQueue();
            Settings = settings;
            _workers = Enumerable.Range(1, settings.NumThreads).Select(workerId => new PoolWorker(this, workerId)).ToArray();

            // Note:
            // The DedicatedThreadPoolSupervisor was removed because aborting thread could lead to unexpected behavior
            // If a new implementation is done, it should spawn a new thread when a worker is not making progress and
            // try to keep {settings.NumThreads} active threads.
        }

        public DedicatedThreadPoolSettings Settings { get; private set; }

        private readonly ThreadPoolWorkQueue _workQueue;
        private readonly PoolWorker[] _workers;

        /// <summary></summary>
        /// <exception cref="ArgumentNullException">
        /// This exception is thrown if the given <paramref name="work"/> item is undefined.
        /// </exception>
        public bool QueueUserWorkItem(Action work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work), "Work item cannot be null.");

            return _workQueue.TryAdd(work);
        }

        public bool QueueSystemWorkItem(Action work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work), "Work item cannot be null.");

            return _workQueue.TryAddS(work);
        }

        public void Dispose()
        {
            _workQueue.CompleteAdding();
        }

        public void WaitForThreadsExit()
        {
            WaitForThreadsExit(Timeout.InfiniteTimeSpan);
        }

        public void WaitForThreadsExit(TimeSpan timeout)
        {
            Task.WaitAll(_workers.Select(worker => worker.ThreadExit).ToArray(), timeout);
        }

        #region Pool worker implementation

        private class PoolWorker
        {
            private readonly DedicatedThreadPool _pool;

            private readonly TaskCompletionSource<object> _threadExit;

            public Task ThreadExit
            {
                get { return _threadExit.Task; }
            }

            public PoolWorker(DedicatedThreadPool pool, int workerId)
            {
                _pool = pool;
                _threadExit = new TaskCompletionSource<object>();

                var thread = new Thread(RunThread, pool.Settings.ThreadMaxStackSize);

                thread.IsBackground = pool.Settings.ThreadType == ThreadType.Background;

                if (pool.Settings.Name != null)
                    thread.Name = string.Format("{0}_{1}", pool.Settings.Name, workerId);

                if (pool.Settings.ApartmentState != ApartmentState.Unknown)
                    thread.SetApartmentState(pool.Settings.ApartmentState);

                thread.Start();
            }

            private void RunThread()
            {
                try
                {
                    foreach (var action in _pool._workQueue.GetConsumingEnumerable())
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            _pool.Settings.ExceptionHandler(ex);
                        }
                    }
                }
                finally
                {
                    _threadExit.TrySetResult(null);
                }
            }
        }

        #endregion

        #region WorkQueue implementation

        private class ThreadPoolWorkQueue
        {
            private static readonly int ProcessorCount = Environment.ProcessorCount;
            private const int CompletedState = 1;

            private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
            private readonly ConcurrentQueue<Action> _queues = new ConcurrentQueue<Action>();
            private readonly UnfairSemaphore _semaphore = new UnfairSemaphore();
            private int _outstandingRequests;
            private int _isAddingCompleted;

            public bool IsAddingCompleted
            {
                get { return Volatile.Read(ref _isAddingCompleted) == CompletedState; }
            }

            public bool TryAdd(Action work)
            {
                // If TryAdd returns true, it's garanteed the work item will be executed.
                // If it returns false, it's also garanteed the work item won't be executed.

                if (IsAddingCompleted)
                    return false;

                _queue.Enqueue(work);
                EnsureThreadRequested();

                return true;
            }
            public bool TryAddS(Action work)
            {
                // If TryAdd returns true, it's garanteed the work item will be executed.
                // If it returns false, it's also garanteed the work item won't be executed.

                if (IsAddingCompleted)
                    return false;

                _queues.Enqueue(work);
                EnsureThreadRequested();

                return true;
            }
            public IEnumerable<Action> GetConsumingEnumerable()
            {
                while (true)
                {
                    Action work;
                    if (_queues.TryDequeue(out work))
                    {
                        yield return work;
                        continue;
                    }

                    if (_queue.TryDequeue(out work))
                    {
                        yield return work;
                    }
                    else if (IsAddingCompleted)
                    {
                        while (_queue.TryDequeue(out work))
                            yield return work;

                        break;
                    }
                    else
                    {
                        _semaphore.Wait();
                        MarkThreadRequestSatisfied();
                    }
                }
            }

            public void CompleteAdding()
            {
                int previousCompleted = Interlocked.Exchange(ref _isAddingCompleted, CompletedState);

                if (previousCompleted == CompletedState)
                    return;

                // When CompleteAdding() is called, we fill up the _outstandingRequests and the semaphore
                // This will ensure that all threads will unblock and try to execute the remaining item in
                // the queue. When IsAddingCompleted is set, all threads will exit once the queue is empty.

                while (true)
                {
                    int count = Volatile.Read(ref _outstandingRequests);
                    int countToRelease = UnfairSemaphore.MaxWorker - count;

                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, UnfairSemaphore.MaxWorker, count);

                    if (prev == count)
                    {
                        _semaphore.Release((short)countToRelease);
                        break;
                    }
                }
            }

            private void EnsureThreadRequested()
            {
                // There is a double counter here (_outstandingRequest and _semaphore)
                // Unfair semaphore does not support value bigger than short.MaxValue,
                // tring to Release more than short.MaxValue could fail miserably.

                // The _outstandingRequest counter ensure that we only request a
                // maximum of {ProcessorCount} to the semaphore.

                // It's also more efficient to have two counter, _outstandingRequests is
                // more lightweight than the semaphore.

                // This trick is borrowed from the .Net ThreadPool
                // https://github.com/dotnet/coreclr/blob/bc146608854d1db9cdbcc0b08029a87754e12b49/src/mscorlib/src/System/Threading/ThreadPool.cs#L568

                int count = Volatile.Read(ref _outstandingRequests);
                while (count < ProcessorCount)
                {
                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, count + 1, count);
                    if (prev == count)
                    {
                        _semaphore.Release();
                        break;
                    }
                    count = prev;
                }
            }

            private void MarkThreadRequestSatisfied()
            {
                int count = Volatile.Read(ref _outstandingRequests);
                while (count > 0)
                {
                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, count - 1, count);
                    if (prev == count)
                    {
                        break;
                    }
                    count = prev;
                }
            }
        }

        #endregion

        #region UnfairSemaphore implementation

        // This class has been translated from:
        // https://github.com/dotnet/coreclr/blob/97433b9d153843492008652ff6b7c3bf4d9ff31c/src/vm/win32threadpool.h#L124

        // UnfairSemaphore is a more scalable semaphore than Semaphore.  It prefers to release threads that have more recently begun waiting,
        // to preserve locality.  Additionally, very recently-waiting threads can be released without an addition kernel transition to unblock
        // them, which reduces latency.
        //
        // UnfairSemaphore is only appropriate in scenarios where the order of unblocking threads is not important, and where threads frequently
        // need to be woken.

        [StructLayout(LayoutKind.Sequential)]
        private sealed class UnfairSemaphore
        {
            public const int MaxWorker = 0x7FFF;

            // We track everything we care about in a single 64-bit struct to allow us to 
            // do CompareExchanges on this for atomic updates.
            [StructLayout(LayoutKind.Explicit)]
            private struct SemaphoreState
            {
                //how many threads are currently spin-waiting for this semaphore?
                [FieldOffset(0)]
                public short Spinners;

                //how much of the semaphore's count is availble to spinners?
                [FieldOffset(2)]
                public short CountForSpinners;

                //how many threads are blocked in the OS waiting for this semaphore?
                [FieldOffset(4)]
                public short Waiters;

                //how much count is available to waiters?
                [FieldOffset(6)]
                public short CountForWaiters;

                [FieldOffset(0)]
                public long RawData;
            }

            [StructLayout(LayoutKind.Explicit, Size = 64)]
            private struct CacheLinePadding
            { }

            private readonly Semaphore m_semaphore;

            // padding to ensure we get our own cache line
#pragma warning disable 169
            private readonly CacheLinePadding m_padding1;
            private SemaphoreState m_state;
            private readonly CacheLinePadding m_padding2;
#pragma warning restore 169

            public UnfairSemaphore()
            {
                m_semaphore = new Semaphore(0, short.MaxValue);
            }

            public bool Wait()
            {
                return Wait(Timeout.InfiniteTimeSpan);
            }
            SpinWait sp = new SpinWait();
            public bool Wait(TimeSpan timeout)
            {
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    // First, just try to grab some count.
                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        // No count available, become a spinner
                        ++newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            break;
                    }
                }

                //
                // Now we're a spinner.  
                //
                int numSpins = 0;
                const int spinLimitPerProcessor = 50;
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        --newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        double spinnersPerProcessor = (double)currentCounts.Spinners / Environment.ProcessorCount;
                        int spinLimit = (int)((spinLimitPerProcessor / spinnersPerProcessor) + 0.5);
                        if (numSpins >= spinLimit)
                        {
                            --newCounts.Spinners;
                            ++newCounts.Waiters;
                            if (TryUpdateState(newCounts, currentCounts))
                                break;
                        }
                        else
                        {
                            //
                            // We yield to other threads using Thread.Sleep(0) rather than the more traditional Thread.Yield().
                            // This is because Thread.Yield() does not yield to threads currently scheduled to run on other
                            // processors.  On a 4-core machine, for example, this means that Thread.Yield() is only ~25% likely
                            // to yield to the correct thread in some scenarios.
                            // Thread.Sleep(0) has the disadvantage of not yielding to lower-priority threads.  However, this is ok because
                            // once we've called this a few times we'll become a "waiter" and wait on the Semaphore, and that will
                            // yield to anything that is runnable.
                            //

                            Thread.Sleep(0);
                            numSpins++;
                        }
                    }
                }

                //
                // Now we're a waiter
                //
                bool waitSucceeded = m_semaphore.WaitOne(timeout);

                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    --newCounts.Waiters;

                    if (waitSucceeded)
                        --newCounts.CountForWaiters;

                    if (TryUpdateState(newCounts, currentCounts))
                        return waitSucceeded;
                }
            }

            public void Release()
            {
                Release(1);
            }

            public void Release(short count)
            {
                while (true)
                {
                    SemaphoreState currentState = GetCurrentState();
                    SemaphoreState newState = currentState;

                    short remainingCount = count;

                    // First, prefer to release existing spinners,
                    // because a) they're hot, and b) we don't need a kernel
                    // transition to release them.
                    short spinnersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Spinners - currentState.CountForSpinners)));
                    newState.CountForSpinners += spinnersToRelease;
                    remainingCount -= spinnersToRelease;

                    // Next, prefer to release existing waiters
                    short waitersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Waiters - currentState.CountForWaiters)));
                    newState.CountForWaiters += waitersToRelease;
                    remainingCount -= waitersToRelease;

                    // Finally, release any future spinners that might come our way
                    newState.CountForSpinners += remainingCount;

                    // Try to commit the transaction
                    if (TryUpdateState(newState, currentState))
                    {
                        // Now we need to release the waiters we promised to release
                        if (waitersToRelease > 0)
                            m_semaphore.Release(waitersToRelease);

                        break;
                    }
                }
            }

            private bool TryUpdateState(SemaphoreState newState, SemaphoreState currentState)
            {
                if (Interlocked.CompareExchange(ref m_state.RawData, newState.RawData, currentState.RawData) == currentState.RawData)
                {
                    Debug.Assert(newState.CountForSpinners <= MaxWorker, "CountForSpinners is greater than MaxWorker");
                    Debug.Assert(newState.CountForSpinners >= 0, "CountForSpinners is lower than zero");
                    Debug.Assert(newState.Spinners <= MaxWorker, "Spinners is greater than MaxWorker");
                    Debug.Assert(newState.Spinners >= 0, "Spinners is lower than zero");
                    Debug.Assert(newState.CountForWaiters <= MaxWorker, "CountForWaiters is greater than MaxWorker");
                    Debug.Assert(newState.CountForWaiters >= 0, "CountForWaiters is lower than zero");
                    Debug.Assert(newState.Waiters <= MaxWorker, "Waiters is greater than MaxWorker");
                    Debug.Assert(newState.Waiters >= 0, "Waiters is lower than zero");
                    Debug.Assert(newState.CountForSpinners + newState.CountForWaiters <= MaxWorker, "CountForSpinners + CountForWaiters is greater than MaxWorker");

                    return true;
                }

                return false;
            }

            private SemaphoreState GetCurrentState()
            {
                // Volatile.Read of a long can get a partial read in x86 but the invalid
                // state will be detected in TryUpdateState with the CompareExchange.

                SemaphoreState state = new SemaphoreState();
                state.RawData = Volatile.Read(ref m_state.RawData);
                return state;
            }
        }

        #endregion
    }
}
