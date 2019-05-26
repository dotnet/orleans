using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Timers;

namespace Grains.Tests.Hosted
{
    /// <summary>
    /// Implements a fake timer registry to facilitate unit tests using the test cluster.
    /// </summary>
    public class FakeTimerRegistry : ITimerRegistry
    {
        /// <summary>
        /// We dont have a ConcurrentHashSet yet so this does the job.
        /// </summary>
        private readonly ConcurrentDictionary<FakeTimerEntry, FakeTimerEntry> timers = new ConcurrentDictionary<FakeTimerEntry, FakeTimerEntry>();

        /// <summary>
        /// Registers a new fake timer entry and returns it.
        /// Note how we are capturing the activation task scheduler to ensure we can tick the fake timers within the activation context.
        /// </summary>
        public IDisposable RegisterTimer(Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimerEntry(this, TaskScheduler.Current, grain, asyncCallback, state, dueTime, period);
            timers[timer] = timer;
            return timer;
        }

        /// <summary>
        /// Returns all fake timer entries.
        /// </summary>
        public IEnumerable<FakeTimerEntry> GetAll() => timers.Keys.ToList();

        /// <summary>
        /// Removes a timer.
        /// </summary>
        protected void Remove(FakeTimerEntry entry) => timers.TryRemove(entry, out _);

        /// <summary>
        /// Implements a fake timer entry to facilitate unit testing.
        /// </summary>
        public class FakeTimerEntry : IDisposable
        {
            private readonly TaskScheduler scheduler;
            private readonly FakeTimerRegistry owner;
            public Grain Grain { get; }
            public Func<object, Task> AsyncCallback { get; }
            public object State { get; }
            public TimeSpan DueTime { get; }
            public TimeSpan DuePeriod { get; }

            public FakeTimerEntry(FakeTimerRegistry owner, TaskScheduler scheduler, Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
            {
                this.scheduler = scheduler;
                this.owner = owner;

                Grain = grain;
                AsyncCallback = asyncCallback;
                State = state;
                DueTime = dueTime;
                DuePeriod = period;
            }

            /// <summary>
            /// Ticks the timer action within the activation context.
            /// </summary>
            public async Task TickAsync() => await await Task.Factory.StartNew(AsyncCallback, State, default, TaskCreationOptions.None, scheduler);

            public void Dispose()
            {
                try
                {
                    owner.Remove(this);
                }
                catch (Exception)
                {
                    // noop
                }
            }
        }
    }
}