using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;


namespace Orleans.Runtime.Scheduler
{
    internal class WorkerPool : IDisposable
    {
        private Semaphore threadLimitingSemaphore;
        private readonly HashSet<WorkerPoolThread> pool;
        private readonly WorkerPoolThread systemThread;
        private readonly OrleansTaskScheduler scheduler;
        private readonly object lockable;
        private bool running;
        private int runningThreadCount;
        private int createThreadCount;
        private SafeTimer longTurnTimer;

        internal readonly int MaxActiveThreads;
        internal readonly TimeSpan MaxWorkQueueWait;
        internal readonly bool EnableWorkerThreadInjection;

        internal bool ShouldInjectWorkerThread { get { return EnableWorkerThreadInjection && runningThreadCount < WorkerPoolThread.MAX_THREAD_COUNT_TO_REPLACE; } }

        internal WorkerPool(OrleansTaskScheduler sched, int maxActiveThreads, bool enableWorkerThreadInjection)
        {
            scheduler = sched;
            MaxActiveThreads = maxActiveThreads;
            EnableWorkerThreadInjection = enableWorkerThreadInjection;
            MaxWorkQueueWait = TimeSpan.FromMilliseconds(50);
            if (EnableWorkerThreadInjection)
            {
                threadLimitingSemaphore = new Semaphore(maxActiveThreads, maxActiveThreads);
            }
            pool = new HashSet<WorkerPoolThread>();
            createThreadCount = 0;
            lockable = new object();
            for (createThreadCount = 0; createThreadCount < MaxActiveThreads; createThreadCount++)
            {
                var t = new WorkerPoolThread(this, scheduler, createThreadCount);
                pool.Add(t);
            }
            createThreadCount++;
            systemThread = new WorkerPoolThread(this, scheduler, createThreadCount, true);
            running = false;
            runningThreadCount = 0;
            longTurnTimer = null;
        }

        internal void Start()
        {
            running = true;
            systemThread.Start();
            foreach (WorkerPoolThread t in pool)
                t.Start();
            
            if (EnableWorkerThreadInjection)
                longTurnTimer = new SafeTimer(obj => CheckForLongTurns(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        internal void Stop()
        {
            running = false;
            if (longTurnTimer != null)
            {
                longTurnTimer.Dispose();
                longTurnTimer = null;
            }

            WorkerPoolThread[] threads;
            lock (lockable)
            {
                threads = pool.ToArray<WorkerPoolThread>();
            }

            foreach (WorkerPoolThread thread in threads)
                thread.Stop();
            
            systemThread.Stop();
        }

        internal void TakeCpu()
        {
            // maintain the threadLimitingSemaphore ONLY if thread injection is enabled.
            if (EnableWorkerThreadInjection)
                threadLimitingSemaphore.WaitOne();
        }

        internal void PutCpu()
        {
            if (EnableWorkerThreadInjection)
                threadLimitingSemaphore.Release();
        }

        internal void RecordRunningThread()
        {
            // maintain the runningThreadCount ONLY if thread injection is enabled.
            if (EnableWorkerThreadInjection)
                Interlocked.Increment(ref runningThreadCount);
        }

        internal void RecordIdlingThread()
        {
            if (EnableWorkerThreadInjection)
                Interlocked.Decrement(ref runningThreadCount);
        }

        internal bool CanExit()
        {
            lock (lockable)
            {
                if (running && (pool.Count <= MaxActiveThreads + 2))
                    return false;
            }
            return true;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "tnew continues to execute after this method call returned")]
        internal void RecordLeavingThread(WorkerPoolThread t)
        {
            bool restart = false;
            lock (lockable)
            {
                pool.Remove(t);
                if (running && (pool.Count < MaxActiveThreads + 2))
                    restart = true;

                if (!restart) return;

                createThreadCount++;
                var tnew = new WorkerPoolThread(this, scheduler, createThreadCount);
                tnew.Start();
            }
        }

        internal void CreateNewThread()
        {
            lock (lockable)
            {
                createThreadCount++;
                var t = new WorkerPoolThread(this, scheduler, createThreadCount);
                pool.Add(t);
                t.Start();
            }
        }

        public void Dispose()
        {
            if (threadLimitingSemaphore != null)
            {
                threadLimitingSemaphore.Dispose();
                threadLimitingSemaphore = null;
            }

            GC.SuppressFinalize(this);
        }

        private void CheckForLongTurns()
        {
            List<WorkerPoolThread> currentPool;
            lock (lockable)
            {
                currentPool = pool.ToList();
            }
            foreach (var thread in currentPool)
                thread.CheckForLongTurns();
        }

        internal bool DoHealthCheck()
        {
            bool ok = true;
            // Note that we want to make sure we run DoHealthCheck on each thread even if one of them fails, so we can't just use &&= because of short-circuiting
            lock (lockable)
            {
                foreach (WorkerPoolThread thread in pool)
                    if (!thread.DoHealthCheck())
                        ok = false;
            }
            if (!systemThread.DoHealthCheck())
                ok = false;
            
            return ok;
        }

        public void DumpStatus(StringBuilder sb)
        {
            List<WorkerPoolThread> threads;
            lock (lockable)
            {
                sb.AppendFormat("WorkerPool MaxActiveThreads={0} MaxWorkQueueWait={1} {2}", MaxActiveThreads, MaxWorkQueueWait, running ? "" : "STOPPED").AppendLine();
                sb.AppendFormat(" PoolSize={0} ActiveThreads={1}", pool.Count + 1, runningThreadCount).AppendLine();
                threads = pool.ToList();
            }
            sb.AppendLine("System Thread:");
            systemThread.DumpStatus(sb);
            sb.AppendLine("Worker Threads:");
            foreach (var workerThread in threads)
                workerThread.DumpStatus(sb);
        }
    }
}
