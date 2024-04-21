using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// An internal class for implementing async timers in Orleans.
    /// </summary>
    internal sealed class AsyncTaskSafeTimer : IDisposable
    {
        private const uint MaxSupportedTimeout = 0xfffffffe;

        private readonly Func<object, Task>  _callback;
        private readonly ILogger _logger;
        private Timer _timer;
        private TimeSpan _dueTime;
        private TimeSpan _period;
        private bool _timerStarted;
        private DateTime _previousTickTime;
        private int _totalNumTicks;

        internal AsyncTaskSafeTimer(ILogger logger, Func<object, Task> asyncTaskCallback, object state)
        {
            ArgumentNullException.ThrowIfNull(asyncTaskCallback);

            _callback = asyncTaskCallback;
            _period = Constants.INFINITE_TIMESPAN;
            _dueTime = Constants.INFINITE_TIMESPAN;
            _totalNumTicks = 0;
            _logger = logger;

            _timer = NonCapturingTimer.Create(HandleAsyncTaskTimerCallback, state, Constants.INFINITE_TIMESPAN, Constants.INFINITE_TIMESPAN);
        }

        public bool IsStarted => _timerStarted;

        public void Start(TimeSpan due, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(_timer is null, this);
            if (_timerStarted) throw new InvalidOperationException($"Calling start is not allowed, since it was already created in a started mode with specified due.");
            if (period == TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period), period, "Cannot use TimeSpan.Zero for timer period");

            long dueTm = (long)_dueTime.TotalMilliseconds;
            if (dueTm < -1) throw new ArgumentOutOfRangeException(nameof(due), "The due time must not be less than -1.");
            if (dueTm > MaxSupportedTimeout) throw new ArgumentOutOfRangeException(nameof(due), "The due time interval must be less than 2^32-2.");

            long periodTm = (long)period.TotalMilliseconds;
            if (periodTm < -1) throw new ArgumentOutOfRangeException(nameof(period), "The period must not be less than -1.");
            if (periodTm > MaxSupportedTimeout) throw new ArgumentOutOfRangeException(nameof(period), "The period interval must be less than 2^32-2.");

            _period = period;
            _dueTime = due;
            _timerStarted = true;
            _previousTickTime = DateTime.UtcNow;
            _timer.Change(due, Constants.INFINITE_TIMESPAN);
        }

        public void Stop()
        {
            _period = Constants.INFINITE_TIMESPAN;
            _dueTime = Constants.INFINITE_TIMESPAN;
            _timerStarted = false;
            _timer?.Change(Constants.INFINITE_TIMESPAN, Constants.INFINITE_TIMESPAN);
        }

        public void Dispose()
        {
            DisposeTimer();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal void DisposeTimer()
        {
            _timerStarted = false;
            var result = Interlocked.CompareExchange(ref _timer, null, _timer);
            result?.Dispose();
        }

        public bool CheckTimerFreeze(DateTime lastCheckTime, Func<string> callerName)
        {
            return CheckTimerDelay(_previousTickTime, _totalNumTicks,
                        _dueTime, _period, _logger, () => $"{nameof(AsyncTaskSafeTimer)}.{callerName()}", ErrorCode.Timer_SafeTimerIsNotTicking, true);
        }

        public static bool CheckTimerDelay(DateTime previousTickTime, int totalNumTicks, 
                        TimeSpan dueTime, TimeSpan timerFrequency, ILogger logger, Func<string> getName, ErrorCode errorCode, bool freezeCheck)
        {
            TimeSpan timeSinceLastTick = DateTime.UtcNow - previousTickTime;
            TimeSpan expectedTimeToNextTick = totalNumTicks == 0 ? dueTime : timerFrequency;
            TimeSpan exceptedTimeWithSlack;
            if (expectedTimeToNextTick >= TimeSpan.FromSeconds(6))
            {
                exceptedTimeWithSlack = expectedTimeToNextTick + TimeSpan.FromSeconds(3);
            }
            else
            {
                exceptedTimeWithSlack = expectedTimeToNextTick.Multiply(1.5);
            }
            if (timeSinceLastTick <= exceptedTimeWithSlack) return true;

            // did not tick in the last period.
            var logLevel = freezeCheck ? LogLevel.Error : LogLevel.Warning;
            if (logger.IsEnabled(logLevel))
            {
                var Title = freezeCheck ? "Watchdog Freeze Alert: " : "";
                logger.Log(
                    logLevel,
                    (int)errorCode, "{Title}{Name} did not fire on time. Last fired at {LastFired}, {TimeSinceLastTick} since previous fire, should have fired after {ExpectedTimeToNextTick}.",
                    Title,
                    getName?.Invoke() ?? "",
                    LogFormatter.PrintDate(previousTickTime),
                    timeSinceLastTick,
                    expectedTimeToNextTick);
            }

            return false;
        }

        /// <summary>
        /// Changes the start time and the interval between method invocations for a timer, using TimeSpan values to measure time intervals.
        /// </summary>
        /// <param name="newDueTime">A TimeSpan representing the amount of time to delay before invoking the callback method specified when the Timer was constructed. Specify negative one (-1) milliseconds to prevent the timer from restarting. Specify zero (0) to restart the timer immediately.</param>
        /// <param name="period">The time interval between invocations of the callback method specified when the Timer was constructed. Specify negative one (-1) milliseconds to disable periodic signaling.</param>
        /// <returns><c>true</c> if the timer was successfully updated; otherwise, <c>false</c>.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private bool Change(TimeSpan newDueTime, TimeSpan period)
        {
            if (period == TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period), period, "Cannot use TimeSpan.Zero for timer period.");

            if (_timer == null) return false;

            _period = period;

            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug((int)ErrorCode.TimerChanging, "Changing timer to DueTime={DueTime} Period={Period}.", newDueTime, period);

            try
            {
                // Queue first new timer tick
                return _timer.Change(newDueTime, Constants.INFINITE_TIMESPAN);
            }
            catch (Exception exc)
            {
                _logger.LogWarning((int)ErrorCode.TimerChangeError, exc, "Error changing timer period - timer not changed.");
                return false;
            }
        }

        private async void HandleAsyncTaskTimerCallback(object state)
        {
            if (_timer == null) return;

            // There is a subtle race/issue here w.r.t unobserved promises.
            // It may happen than the callback will resolve some promises on which the higher level application code is depends upon
            // and this promise's await  will fire before the below code (after await or Finally) even runs.
            // In the unit test case this may lead to the situation where unit test has finished, but p1 or p2 or p3 have not been observed yet.
            // To properly fix this we may use a mutex/monitor to delay execution of asyncCallbackFunc until all CWs and Finally in the code below were scheduled 
            // (not until CW lambda was run, but just until CW function itself executed). 
            // This however will relay on scheduler executing these in separate threads to prevent deadlock, so needs to be done carefully. 
            // In particular, need to make sure we execute asyncCallbackFunc in another thread (so use StartNew instead of ExecuteWithSafeTryCatch).

            try
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace((int)ErrorCode.TimerBeforeCallback, "About to make async task timer callback for timer.");
                await _callback(state);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace((int)ErrorCode.TimerAfterCallback, "Completed async task timer callback.");
            }
            catch (Exception exc)
            {
                _logger.LogWarning((int)ErrorCode.TimerCallbackError, exc, "Ignored exception during async task timer callback.");
            }
            finally
            {
                _previousTickTime = DateTime.UtcNow;

                // Queue next timer callback
                QueueNextTimerTick();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void QueueNextTimerTick()
        {
            try
            {
                if (_timer == null) return;

                _totalNumTicks++;

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace((int)ErrorCode.TimerChanging, "About to queue next tick for timer.");

                if (_period == Constants.INFINITE_TIMESPAN)
                {
                    DisposeTimer();

                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace((int)ErrorCode.TimerStopped, "Timer is now stopped and disposed.");
                }
                else
                {
                    _timer.Change(_period, Constants.INFINITE_TIMESPAN);

                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace((int)ErrorCode.TimerNextTick, "Queued next tick for timer in {TimerFrequency}.", _period);
                }
            }
            catch (ObjectDisposedException ode)
            {
                _logger.LogWarning((int)ErrorCode.TimerDisposeError, ode, "Timer already disposed - will not queue next timer tick.");
            }
            catch (Exception exc)
            {
                _logger.LogError((int)ErrorCode.TimerQueueTickError, exc, "Error queueing next timer tick - WARNING: timer is now stopped.");
            }
        }
    }
}
