﻿using System;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// General pattern for an asynchronous worker that performs a work task, when notified,
    /// to service queued work. Each work cycle handles ALL the queued work. 
    /// If new work arrives during a work cycle, another cycle is scheduled. 
    /// The worker never executes more than one instance of the work cycle at a time, 
    /// and consumes no resources when idle. It uses TaskScheduler.Current 
    /// to schedule the work cycles.
    /// </summary>
    public abstract class BatchWorker
    {
        // Subclass overrides this to define what constitutes a work cycle
        protected abstract Task Work();

        /// <summary>
        /// Notify the worker that there is more work.
        /// </summary>
        public void Notify()
        {
            lock (this)
            {
                if (currentWorkCycle != null)
                {
                    // lets the current work cycle know that there is more work
                    moreWork = true;
                }
                else
                {
                    // start a work cycle
                    Start();
                }
            }
        }

        // task for the current work cycle, or null if idle
        private volatile Task currentWorkCycle;
 
        // flag is set to indicate that more work has arrived during execution of the task
        private volatile bool moreWork;

        // used to communicate the task for the next work cycle to waiters
        // is non-null only if there are waiters
        private TaskCompletionSource<Task> nextWorkCyclePromise;

        private void Start()
        {
            // start the task that is doing the work
            currentWorkCycle = Work();

            // chain a continuation that checks for more work, on the same scheduler
            currentWorkCycle.ContinueWith(t => this.CheckForMoreWork(), TaskScheduler.Current);
        }

        // executes at the end of each work cycle
        // on the same task scheduler
        private void CheckForMoreWork()
        {
            Action signalThunk = null;

            lock (this)
            {
                if (moreWork)
                {
                    moreWork = false;

                    // see if someone created a promise for waiting for the next work cycle
                    // if so, take it and remove it
                    var x = this.nextWorkCyclePromise;
                    this.nextWorkCyclePromise = null;

                    // start the next work cycle
                    Start();

                    // if someone is waiting, signal them
                    if (x != null)
                        signalThunk = () => { x.SetResult(currentWorkCycle); };
                }
                else
                {
                    currentWorkCycle = null;
                }
            }

            // to be safe, must do the signalling out here so it is not under the lock
            if (signalThunk != null)
                signalThunk();
        }

        /// <summary>
        /// Check if this worker is busy.
        /// </summary>
        /// <returns></returns>
        public bool IsIdle()
        {
            // no lock needed for reading volatile field
            return currentWorkCycle == null;
        }

        /// <summary>
        /// Wait for the current work cycle, and also the next work cycle if there is currently unserviced work.
        /// </summary>
        /// <returns></returns>
        public async Task WaitForCurrentWorkToBeServiced()
        {
            Task<Task> waitfortasktask = null;
            Task waitfortask = null;

            // figure out exactly what we need to wait for
            lock (this)
            {
                if (!moreWork)
                    // just wait for current work cycle
                    waitfortask = currentWorkCycle;
                else
                {
                    // we need to wait for the next work cycle
                    // but that task does not exist yet, so we use a promise that signals when the next work cycle is launched
                    if (nextWorkCyclePromise == null)
                        nextWorkCyclePromise = new TaskCompletionSource<Task>();
                    waitfortasktask = nextWorkCyclePromise.Task;
                }
            }

            // now do the actual waiting outside of the lock

            if (waitfortasktask != null)
                await await waitfortasktask;

            else if (waitfortask != null)
                await waitfortask;
        }
    }
}