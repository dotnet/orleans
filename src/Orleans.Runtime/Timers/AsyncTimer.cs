using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class AsyncTimer : IAsyncTimer
    {
        /// <summary>
        /// Timers can fire up to 3 seconds late before a warning is emitted and the instance is deemed unhealthy.
        /// </summary>
        private static readonly TimeSpan TimerDelaySlack = TimeSpan.FromSeconds(3);

        private readonly Task cancellationTask;
        private readonly CancellationTokenSource cancellation;
        private readonly TimeSpan period;
        private readonly ILogger log;
        private bool disposed;
        private DateTime lastFired = DateTime.MinValue;
        private bool wasDelayed;

        public AsyncTimer(TimeSpan period, ILogger log)
        {
            this.cancellation = new CancellationTokenSource();
            this.cancellationTask = this.cancellation.Token.WhenCancelled();
            this.log = log;
            this.period = period;
        }

        /// <summary>
        /// Returns a task which completes after the required delay.
        /// </summary>
        /// <param name="overrideDelay">An optional override to this timer's configured period.</param>
        /// <returns><see langword="true"/> if the timer completed or <see langword="false"/> if the timer was cancelled</returns>
        public async Task<bool> NextTick(TimeSpan? overrideDelay = default)
        {
            if (this.cancellationTask.IsCompleted) return false;

            var now = DateTime.UtcNow;
            TimeSpan delay;
            if (overrideDelay.HasValue)
            {
                delay = overrideDelay.Value;
            }
            else
            {
                if (this.lastFired == DateTime.MinValue)
                {
                    delay = this.period;
                }
                else
                {
                    delay = this.lastFired.Add(this.period).Subtract(now);
                }
            }

            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            if (delay > TimeSpan.Zero)
            {
                var resultTask = await Task.WhenAny(this.cancellationTask, Task.Delay(delay));
                if (ReferenceEquals(resultTask, this.cancellationTask)) return false;
            }

            var actual = this.lastFired = DateTime.UtcNow;
            var dueTime = now.Add(delay);
            var overshoot = actual.Subtract(dueTime).Duration();
            this.wasDelayed = overshoot > TimerDelaySlack;
            if (this.wasDelayed)
            {
                this.log?.LogWarning(
                    "Timer should have fired at {Expected} but fired at {Actual}, which is {Overshoot} longer than expected",
                    dueTime,
                    actual,
                    overshoot);
            }

            return true;
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            if (this.lastFired > lastCheckTime) return !this.wasDelayed;
            return true;
        }

        public void Cancel() => this.cancellation.Cancel(throwOnFirstException: false);

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                this.Cancel();
                this.cancellation.Dispose();
            }
        }
    }
}
