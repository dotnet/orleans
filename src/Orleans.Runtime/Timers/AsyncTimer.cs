using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;

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
        private DateTime lastFired = DateTime.MinValue;
        private DateTime? expected;

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

            var start = DateTime.UtcNow;
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
                    delay = this.lastFired.Add(this.period).Subtract(start);
                }
            }

            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            var dueTime = start.Add(delay);
            this.expected = dueTime;
            if (delay > TimeSpan.Zero)
            {
                var resultTask = await Task.WhenAny(this.cancellationTask, Task.Delay(delay));
                if (ReferenceEquals(resultTask, this.cancellationTask)) return false;
            }

            var now = this.lastFired = DateTime.UtcNow;
            var overshoot = GetOvershootDelay(now, dueTime);
            if (overshoot > TimeSpan.Zero)
            {
                this.log?.LogWarning(
                    "Timer should have fired at {DueTime} but fired at {CurrentTime}, which is {Overshoot} longer than expected",
                    dueTime,
                    now,
                    overshoot);
            }

            return true;
        }

        private static TimeSpan GetOvershootDelay(DateTime now, DateTime dueTime)
        {
            if (dueTime == DateTime.MinValue) return TimeSpan.Zero;
            if (dueTime > now) return TimeSpan.Zero;

            var overshoot = now.Subtract(dueTime);
            if (overshoot > TimerDelaySlack) return overshoot;

            return TimeSpan.Zero;
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            var now = DateTime.UtcNow;
            var dueTime = this.expected.GetValueOrDefault();
            var overshoot = GetOvershootDelay(now, dueTime);
            if (overshoot > TimeSpan.Zero)
            {
                this.log?.LogWarning(
                    "Timer should have fired at {DueTime}, which is {Overshoot} ago",
                    dueTime,
                    overshoot);
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            this.expected = default;
            this.cancellation?.Cancel(throwOnFirstException: false);
        }
    }
}
