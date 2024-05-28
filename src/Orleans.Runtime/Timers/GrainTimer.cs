#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime;

internal sealed class GrainTimer : IGrainTimer, IAsyncDisposable
{
    // PeriodicTimer only supports periods equal to -1ms (infinite timeout) or >= 1ms
    private static readonly TimeSpan MinimumPeriod = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond);
    private readonly PeriodicTimer _timer;
    private readonly Func<object?, Task> _callback;
    private readonly ILogger _logger;
    private readonly IGrainContext _grainContext;
    private readonly Task _processingTask;
    private readonly object? _state;
    private TimeSpan _dueTime;
    private TimeSpan _period;
    private bool _changed;

    public GrainTimer(IGrainContext grainContext, ILogger logger, Func<object?, Task> callback, object? state, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(callback);

        if (RuntimeContext.Current is null)
        {
            ThrowInvalidSchedulingContext();
        }

        if (!Equals(RuntimeContext.Current, grainContext))
        {
            ThrowIncorrectGrainContext();
        }

        // Avoid capturing async locals.
        using (new ExecutionContextSuppressor())
        {
            _grainContext = grainContext;
            _logger = logger;
            _callback = callback;
            _timer = new PeriodicTimer(Timeout.InfiniteTimeSpan, timeProvider);
            _state = state;
            _dueTime = Timeout.InfiniteTimeSpan;
            _period = Timeout.InfiniteTimeSpan;
            _processingTask = ProcessTimerTicks();
        }
    }

    [DoesNotReturn]
    private static void ThrowIncorrectGrainContext() => throw new InvalidOperationException("Current grain context differs from specified grain context.");

    [DoesNotReturn]
    private static void ThrowInvalidSchedulingContext()
    {
        throw new InvalidSchedulingContextException(
            "Current grain context is null. "
             + "Please make sure you are not trying to create a Timer from outside Orleans Task Scheduler, "
             + "which will be the case if you create it inside Task.Run.");
    }

    private async Task ProcessTimerTicks()
    {
        // Yield immediately to let the caller continue.
        await Task.Yield();

        while (await _timer.WaitForNextTickAsync())
        {
            _changed = false;
            try
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace((int)ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {TimerName}", GetDiagnosticName());
                }

                await _callback(_state);

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace((int)ErrorCode.TimerAfterCallback, "Completed timer callback for timer {TimerName}", GetDiagnosticName());
                }
            }
            catch (Exception exc)
            {
                _logger.LogError(
                    (int)ErrorCode.Timer_GrainTimerCallbackError,
                    exc,
                    "Caught and ignored exception thrown from timer callback for timer {TimerName}",
                    GetDiagnosticName());
            }

            // Resume regular ticking if the period was not changed during the iteration.
            if (!_changed && _timer.Period != _period)
            {
                try
                {
                    _timer.Period = _period;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    public void Change(TimeSpan dueTime, TimeSpan period)
    {
        ValidateArguments(dueTime, period);

        _changed = true;
        _dueTime = AdjustPeriod(dueTime);
        _period = AdjustPeriod(period);
        _timer.Period = _dueTime;

        static TimeSpan AdjustPeriod(TimeSpan value)
        {
            // Period must be either -1ms (infinite timeout) or >= 1ms
            if (value != Timeout.InfiniteTimeSpan && value <= MinimumPeriod)
            {
                // Adjust period to 1ms if it is out of bounds. In practice,
                // this is smaller than the timer resolution, so the difference is imperceptible.
                return MinimumPeriod;
            }

            return value;
        }
    }

    private static void ValidateArguments(TimeSpan dueTime, TimeSpan period)
    {
        // See https://github.com/dotnet/runtime/blob/78b5f40a60d9e095abb2b0aabd8c062b171fb9ab/src/libraries/System.Private.CoreLib/src/System/Threading/Timer.cs#L824-L825
        const uint MaxSupportedTimeout = 0xfffffffe;

        // See https://github.com/dotnet/runtime/blob/78b5f40a60d9e095abb2b0aabd8c062b171fb9ab/src/libraries/System.Private.CoreLib/src/System/Threading/Timer.cs#L927-L930
        long dueTm = (long)dueTime.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTm, -1, nameof(dueTime));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTm, MaxSupportedTimeout, nameof(dueTime));

        long periodTm = (long)period.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(periodTm, -1, nameof(period));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(periodTm, MaxSupportedTimeout, nameof(period));
    }

    private string GetDiagnosticName() => $"GrainTimer TimerCallbackHandler:{_callback?.Target}->{_callback?.Method}";

    public void Dispose()
    {
        _timer.Dispose();
        _grainContext.GetComponent<IGrainTimerRegistry>()?.OnTimerDisposed(this);
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        await _processingTask;
    }
}
