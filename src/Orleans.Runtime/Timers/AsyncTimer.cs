using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal partial class AsyncTimer : IAsyncTimer
    {
        /// <summary>
        /// Timers can fire up to 3 seconds late before a warning is emitted and the instance is deemed unhealthy.
        /// </summary>
        private static readonly TimeSpan TimerDelaySlack = TimeSpan.FromSeconds(3);

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly TimeSpan period;
        private readonly string name;
        private readonly ILogger log;
        private readonly TimeProvider timeProvider;
        private DateTime lastFired = DateTime.MinValue;
        private DateTime expected;

        public AsyncTimer(TimeSpan period, string name, ILogger log, TimeProvider timeProvider)
        {
            this.log = log;
            this.period = period;
            this.name = name;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// Returns a task which completes after the required delay.
        /// </summary>
        /// <param name="overrideDelay">An optional override to this timer's configured period.</param>
        /// <returns><see langword="true"/> if the timer completed or <see langword="false"/> if the timer was cancelled</returns>
        public async Task<bool> NextTick(TimeSpan? overrideDelay = default)
        {
            if (cancellation.IsCancellationRequested) return false;

            var start = timeProvider.GetUtcNow().UtcDateTime;
            var delay = overrideDelay switch
            {
                { } value => value,
                _ when lastFired == DateTime.MinValue => period,
                _ => lastFired.Add(period).Subtract(start)
            };

            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            var dueTime = start.Add(delay);
            this.expected = dueTime;
            if (delay > TimeSpan.Zero)
            {
                // for backwards compatibility, support timers with periods up to ReminderRegistry.MaxSupportedTimeout
                var maxDelay = TimeSpan.FromMilliseconds(int.MaxValue);
                while (delay > maxDelay)
                {
                    delay -= maxDelay;
                    try
                    {
                        await Task.Delay(maxDelay, timeProvider, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await Task.Yield();
                        expected = default;
                        return false;
                    }
                }

                try
                {
                    await Task.Delay(delay, timeProvider, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await Task.Yield();
                    expected = default;
                    return false;
                }
            }

            var now = this.lastFired = timeProvider.GetUtcNow().UtcDateTime;
            var overshoot = GetOvershootDelay(now, dueTime);
            if (overshoot > TimeSpan.Zero)
            {
                if (!Debugger.IsAttached)
                {
                    LogTimerOvershoot(this.log, dueTime, now, overshoot);
                }
            }

            expected = default;
            return true;
        }

        private static TimeSpan GetOvershootDelay(DateTime now, DateTime dueTime)
        {
            if (dueTime == default) return TimeSpan.Zero;
            if (dueTime > now) return TimeSpan.Zero;

            var overshoot = now.Subtract(dueTime);
            if (overshoot > TimerDelaySlack) return overshoot;

            return TimeSpan.Zero;
        }

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var due = this.expected;
            var overshoot = GetOvershootDelay(now, due);
            if (overshoot > TimeSpan.Zero && !Debugger.IsAttached)
            {
                reason = $"{this.name} timer should have fired at {due}, which is {overshoot} ago";
                return false;
            }

            reason = default;
            return true;
        }

        public void Dispose()
        {
            this.expected = default;
            this.cancellation.Cancel();
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Timer should have fired at {DueTime} but fired at {CurrentTime}, which is {Overshoot} longer than expected"
        )]
        private static partial void LogTimerOvershoot(ILogger logger, DateTime dueTime, DateTime currentTime, TimeSpan overshoot);
    }
}
