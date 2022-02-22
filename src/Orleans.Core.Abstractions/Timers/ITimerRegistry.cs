using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Timers
{
    /// <summary>
    /// Functionality for managing grain timers.
    /// </summary>
    public interface ITimerRegistry
    {
        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grainContext">The grain which the timer is associated with.</param>
        /// <param name="asyncCallback">The timer callback, which will fire whenever the timer becomes due.</param>
        /// <param name="state">The state object passed to the callback.</param>
        /// <param name="dueTime">
        /// The amount of time to delay before the <paramref name="asyncCallback"/> is invoked.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of <paramref name="asyncCallback"/>.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable periodic signalling.
        /// </param>
        /// <returns>
        /// An <see cref="IDisposable"/> object which will cancel the timer upon disposal.
        /// </returns>
        IDisposable RegisterTimer(IGrainContext grainContext, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);
    }
}