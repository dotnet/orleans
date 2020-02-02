using System;
using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    /// <summary>
    /// Demonstrates a grain that performs an internal operation based on a timer.
    /// </summary>
    public class TimerGrain : Grain, ITimerGrain
    {
        private int value;

        public Task<int> GetValueAsync() => Task.FromResult(value);

        public override Task OnActivateAsync()
        {
            RegisterTimer(_ =>
            {
                ++value;
                return Task.CompletedTask;
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            return base.OnActivateAsync();
        }

        /// <summary>
        /// This opens up the timer registration method for mocking.
        /// </summary>
        public virtual new IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period) =>
            base.RegisterTimer(asyncCallback, state, dueTime, period);
    }
}