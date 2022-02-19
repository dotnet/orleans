using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Timers.Internal;

#nullable enable
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
        private readonly object lockable = new object();

        private DateTime? scheduledNotify;

        // Task for the current work cycle, or null if idle
        private Task? currentWorkCycle;

        // Flag is set to indicate that more work has arrived during execution of the task
        private bool moreWork;

        // Used to communicate the task for the next work cycle to waiters.
        // This value is non-null only if there are waiters.
        private TaskCompletionSource<Task>? nextWorkCyclePromise;
        private Task? nextWorkCycle;

        /// <summary>Implement this member in derived classes to define what constitutes a work cycle</summary>
        /// <returns>>
        /// A <see cref="Task"/>
        /// </returns>
        protected abstract Task Work();

        /// <summary>
        /// Gets or sets the cancellation used to cancel this batch worker.
        /// </summary>
        protected CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// Notify the worker that there is more work.
        /// </summary>
        public void Notify()
        {
            lock (lockable)
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

        /// <summary>
        /// Instructs the batch worker to run again to check for work, if
        /// it has not run again already by then, at specified <paramref name="utcTime"/>.
        /// </summary>
        /// <param name="utcTime"></param>
        public void Notify(DateTime utcTime)
        {
            var now = DateTime.UtcNow;

            if (now >= utcTime)
            {
                Notify();
            }
            else
            {
                lock (lockable)
                {
                    if (!scheduledNotify.HasValue || scheduledNotify.Value > utcTime)
                    {
                        scheduledNotify = utcTime;

                        ScheduleNotify(utcTime, now).Ignore();
                    }
                }
            }
        }

        private async Task ScheduleNotify(DateTime time, DateTime now)
        {
            await TimerManager.Delay(time - now, this.CancellationToken);

            if (scheduledNotify == time)
            {
                Notify();
            }
        }

        private Task Start()
        {
            // Clear any scheduled runs
            scheduledNotify = null;

            // Queue a task that is doing the work
            var task = Task.Factory.StartNew(s => ((BatchWorker)s!).Work(), this, default, default, TaskScheduler.Current).Unwrap();
            currentWorkCycle = task;

            // chain a continuation that checks for more work, on the same scheduler
            task.ContinueWith((_, s) => ((BatchWorker)s!).CheckForMoreWork(), this);
            return task;
        }

        /// <summary>
        /// Executes at the end of each work cycle on the same task scheduler.
        /// </summary>
        private void CheckForMoreWork()
        {
            TaskCompletionSource<Task>? signal;
            Task taskToSignal;

            lock (lockable)
            {
                if (moreWork)
                {
                    moreWork = false;

                    // see if someone created a promise for waiting for the next work cycle
                    // if so, take it and remove it
                    signal = this.nextWorkCyclePromise;
                    this.nextWorkCyclePromise = null;
                    this.nextWorkCycle = null;

                    // start the next work cycle
                    taskToSignal = Start();
                }
                else
                {
                    currentWorkCycle = null;
                    return;
                }
            }

            // to be safe, must do the signalling out here so it is not under the lock
            signal?.SetResult(taskToSignal);
        }

        /// <summary>
        /// Check if this worker is idle.
        /// </summary>
        public bool IsIdle() => currentWorkCycle == null;

        /// <summary>
        /// Wait for the current work cycle, and also the next work cycle if there is currently unserviced work.
        /// </summary>
        public Task WaitForCurrentWorkToBeServiced()
        {
            // Figure out exactly what we need to wait for
            lock (lockable)
            {
                if (!moreWork)
                {
                    // Just wait for current work cycle
                    return currentWorkCycle ?? Task.CompletedTask;
                }
                else
                {
                    // we need to wait for the next work cycle
                    // but that task does not exist yet, so we use a promise that signals when the next work cycle is launched
                    return nextWorkCycle ?? CreateNextWorkCyclePromise();
                }
            }
        }

        private Task CreateNextWorkCyclePromise()
        {
            // it's OK to run any continuations synchrnously because this promise only gets signaled at the very end of CheckForMoreWork
            nextWorkCyclePromise = new TaskCompletionSource<Task>();
            return nextWorkCycle = nextWorkCyclePromise.Task.Unwrap();
        }

        /// <summary>
        /// Notify the worker that there is more work, and wait for the current work cycle, and also the next work cycle if there is currently unserviced work.
        /// </summary>
        public Task NotifyAndWaitForWorkToBeServiced()
        {
            lock (lockable)
            {
                if (currentWorkCycle != null)
                {
                    moreWork = true;
                    return nextWorkCycle ?? CreateNextWorkCyclePromise();
                }
                else
                {
                    return Start();
                }
            }
        }
    }

    /// <summary>
    /// A <see cref="BatchWorker"/> implementation which executes a provided delegate as its <see cref="BatchWorker.Work"/> implementation.
    /// </summary>
    public class BatchWorkerFromDelegate : BatchWorker
    {
        private readonly Func<Task> work;

        /// <summary>
        /// Initializes a new <see cref="BatchWorkerFromDelegate"/> instance.
        /// </summary>
        /// <param name="work">The delegate to invoke when <see cref="BatchWorker.Work"/> is invoked.</param>
        /// <param name="cancellationToken">The cancellation token used to stop the worker.</param>
        public BatchWorkerFromDelegate(Func<Task> work, CancellationToken cancellationToken = default(CancellationToken))
        {
            this.work = work;
            this.CancellationToken = cancellationToken;
        }

        /// <inheritdoc />
        protected override Task Work()
        {
            return work();
        }
    }
}