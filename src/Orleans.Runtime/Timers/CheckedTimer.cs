using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class CheckedTimer : IDisposable, IHealthCheckable
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
        private bool wasDelayed;

        public CheckedTimer(
            TimeSpan period,
            ILoggerFactory loggerFactory,
            string name,
            CancellationToken? cancellationToken = default)
        {
            if (cancellationToken is null)
            {
                this.cancellation = new CancellationTokenSource();
                this.cancellationTask = this.cancellation.Token.WhenCancelled();
            }
            else
            {
                this.cancellation = null;
                this.cancellationTask = cancellationToken?.WhenCancelled();
            }

            this.log = loggerFactory.CreateLogger($"{nameof(CheckedTimer)}-{name}");
            this.period = period;
        }

        /// <summary>
        /// Returns a task which completes after the required delay.
        /// </summary>
        /// <param name="delay">An optional override to this timer's configured period.</param>
        /// <returns><see langword="true"/> if the timer completed or <see langword="false"/> if the timer was cancelled</returns>
        public async Task<bool> TickAsync(TimeSpan? delay = default)
        {
            if (this.cancellationTask.IsCompleted) return false;

            // Determine how long to wait for.
            var now = DateTime.UtcNow;
            if (delay == default)
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
                var resultTask = await Task.WhenAny(this.cancellationTask, Task.Delay(delay.Value));
                if (ReferenceEquals(resultTask, this.cancellationTask)) return false;
            }

            var actual = this.lastFired = DateTime.UtcNow;
            var dueTime = now.Add(delay.Value);
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

        public void Dispose() => this.cancellation?.Cancel(throwOnFirstException: false);
    }
}
