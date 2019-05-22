using System;
using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    /// <summary>
    /// Demonstrates a grain that calls an external grain based on a timer.
    /// </summary>
    public class CallingTimerGrain : Grain, ICallingTimerGrain
    {
        private int counter;

        /// <summary>
        /// Orleans calls this on grain activation.
        /// For isolated unit tests we must call this to simulate activation.
        /// However, the test host will call this on its own.
        /// </summary>
        public override Task OnActivateAsync()
        {
            // register a timer to call another grain every second
            RegisterTimer(_ => GrainFactory.GetGrain<ISummaryGrain>(Guid.Empty).SetAsync(GrainKey, counter),
                null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            return base.OnActivateAsync();
        }

        /// <summary>
        /// This opens up the grain key for mocking.
        /// </summary>
        public virtual string GrainKey => this.GetPrimaryKeyString();

        /// <summary>
        /// This opens up the grain factory property for mocking.
        /// </summary>
        public virtual new IGrainFactory GrainFactory =>
            base.GrainFactory;

        /// <summary>
        /// This opens up the timer registration method for mocking.
        /// </summary>
        public virtual new IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period) =>
            base.RegisterTimer(asyncCallback, state, dueTime, period);

        /// <summary>
        /// Increments the counter by one.
        /// </summary>
        public Task IncrementAsync()
        {
            counter += 1;
            return Task.CompletedTask;
        }
    }
}