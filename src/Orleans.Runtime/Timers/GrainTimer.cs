#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Runtime.Internal;
using Orleans.Serialization.Invocation;
using Orleans.Timers;

namespace Orleans.Runtime;

internal abstract partial class GrainTimer : IGrainTimer
{
    protected static readonly GrainInterfaceType InvokableInterfaceType = GrainInterfaceType.Create("Orleans.Runtime.IGrainTimerInvoker");
    protected static readonly TimerCallback TimerCallback = (state) => ((GrainTimer)state!).ScheduleTickOnActivation();
    protected static readonly MethodInfo InvokableMethodInfo = typeof(IGrainTimerInvoker).GetMethod(nameof(IGrainTimerInvoker.InvokeCallbackAsync), BindingFlags.Instance | BindingFlags.Public)!;
    private readonly CancellationTokenSource _cts = new();
    private readonly ITimer _timer;
    private readonly IGrainContext _grainContext;
    private readonly TimerRegistry _shared;
    private readonly bool _interleave;
    private readonly bool _keepAlive;
    private readonly TimerTickInvoker _invoker;
    private bool _changed;
    private bool _firing;
    private TimeSpan _dueTime;
    private TimeSpan _period;

    public GrainTimer(TimerRegistry shared, IGrainContext grainContext, bool interleave, bool keepAlive)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(grainContext);

        _interleave = interleave;
        _keepAlive = keepAlive;
        _shared = shared;
        _grainContext = grainContext;
        _dueTime = Timeout.InfiniteTimeSpan;
        _period = Timeout.InfiniteTimeSpan;
        _invoker = new(this);

        // Avoid capturing async locals.
        using (new ExecutionContextSuppressor())
        {
            _timer = shared.TimeProvider.CreateTimer(TimerCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    protected IGrainContext GrainContext => _grainContext;

    private ILogger Logger => _shared.TimerLogger;

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

    protected void ScheduleTickOnActivation()
    {
        try
        {
            // Indicate that the timer is firing so that the effect of the next change call is deferred until after the tick completes.
            _firing = true;

            // Note: this does not execute on the activation's execution context.
            var msg = _shared.MessageFactory.CreateMessage(body: _invoker, options: InvokeMethodOptions.OneWay);
            msg.SetInfiniteTimeToLive();
            msg.SendingGrain = _grainContext.GrainId;
            msg.TargetGrain = _grainContext.GrainId;
            msg.SendingSilo = _shared.LocalSiloDetails.SiloAddress;
            msg.TargetSilo = _shared.LocalSiloDetails.SiloAddress;
            msg.InterfaceType = InvokableInterfaceType;
            msg.IsKeepAlive = _keepAlive;
            msg.IsAlwaysInterleave = _interleave;

            // Prevent the message from being forwarded in the case of deactivation.
            msg.IsLocalOnly = true;

            _grainContext.ReceiveMessage(msg);
        }
        catch (Exception exception)
        {
            try
            {
                LogErrorScheduleTickOnActivation(Logger, exception, this);
            }
            catch
            {
                // Ignore.
                // Allowing an exception to escape here would crash the process.
            }
        }
    }

    protected abstract Task InvokeCallbackAsync(CancellationToken cancellationToken);

    private ValueTask<Response> InvokeGrainTimerCallbackAsync()
    {
        try
        {
            LogTraceBeforeCallback(Logger, this);

            _changed = false;
            var task = InvokeCallbackAsync(_cts.Token);

            // If the task is not completed, we need to await the tick asynchronously.
            if (task is { IsCompletedSuccessfully: false })
            {
                // Complete asynchronously.
                return AwaitCallbackTask(task);
            }
            else
            {
                // Complete synchronously.
                LogTraceAfterCallback(Logger, this);

                OnTickCompleted();
                return new(Response.Completed);
            }
        }
        catch (Exception exc)
        {
            OnTickCompleted();
            return new(OnCallbackException(exc));
        }
    }

    private void OnTickCompleted()
    {
        // Schedule the next tick.
        try
        {
            if (_cts.IsCancellationRequested)
            {
                // The instance has been disposed. No further ticks should be fired.
                return;
            }

            if (!_changed)
            {
                // If the timer was not modified during the tick, schedule the next tick based on the period.
                _timer.Change(_period, Timeout.InfiniteTimeSpan);
            }
            else
            {
                // If the timer was modified during the tick, schedule the next tick based on the new due time.
                _timer.Change(_dueTime, Timeout.InfiniteTimeSpan);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _firing = false;
        }
    }

    private Response OnCallbackException(Exception exc)
    {
        LogWarningCallbackException(Logger, exc, this);
        return Response.FromException(exc);
    }

    private async ValueTask<Response> AwaitCallbackTask(Task task)
    {
        try
        {
            await task;

            LogTraceAfterCallback(Logger, this);

            return Response.Completed;
        }
        catch (Exception exc)
        {
            return OnCallbackException(exc);
        }
        finally
        {
            OnTickCompleted();
        }
    }

    public void Change(TimeSpan dueTime, TimeSpan period)
    {
        ValidateArguments(dueTime, period);

        _changed = true;
        _dueTime = dueTime;
        _period = period;

        // If the timer is currently firing, the change will be deferred until after the tick completes.
        // Otherwise, perform the change now.
        if (!_firing)
        {
            try
            {
                // This method resets the timer, so the next tick will be scheduled at the new due time and subsequent
                // ticks will be scheduled after the specified period.
                _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
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

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (Exception exception)
        {
            LogErrorCancellingCallback(Logger, exception);
        }

        _timer.Dispose();
        var timerRegistry = _grainContext.GetComponent<IGrainTimerRegistry>();
        timerRegistry?.OnTimerDisposed(this);
    }

    public override string ToString() => $"[{GetType()}] Grain: '{_grainContext}'";

    private sealed class TimerTickInvoker(GrainTimer timer) : IInvokable, IGrainTimerInvoker
    {
        public object? GetTarget() => this;

        public void SetTarget(ITargetHolder holder)
        {
            if (timer._grainContext != holder)
            {
                throw new InvalidOperationException($"Invalid target holder. Expected {timer._grainContext}, received {holder}.");
            }
        }

        public ValueTask<Response> Invoke() => timer.InvokeGrainTimerCallbackAsync();

        // This method is declared for the sake of IGrainTimerCore, but it is not intended to be called directly.
        // It exists for grain call interceptors which inspect the implementation method.
        Task IGrainTimerInvoker.InvokeCallbackAsync() => throw new InvalidOperationException();

        public int GetArgumentCount() => 0;

        public object? GetArgument(int index) => throw new IndexOutOfRangeException();

        public void SetArgument(int index, object value) => throw new IndexOutOfRangeException();

        public string GetMethodName() => nameof(IGrainTimerInvoker.InvokeCallbackAsync);

        public string GetInterfaceName() => nameof(IGrainTimerInvoker);

        public string GetActivityName() => $"{nameof(IGrainTimerInvoker)}/{nameof(IGrainTimerInvoker.InvokeCallbackAsync)}";

        public MethodInfo GetMethod() => InvokableMethodInfo;

        public Type GetInterfaceType() => typeof(IGrainTimerInvoker);

        public TimeSpan? GetDefaultResponseTimeout() => null;

        public void Dispose()
        {
            // Do nothing. Instances are disposed after invocation, but this instance will be reused for the lifetime of the timer.
        }

        public override string ToString() => timer.ToString();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error invoking timer tick for timer '{Timer}'."
    )]
    private static partial void LogErrorScheduleTickOnActivation(ILogger logger, Exception exception, GrainTimer timer);

    [LoggerMessage(
        EventId = (int)ErrorCode.TimerBeforeCallback,
        Level = LogLevel.Trace,
        Message = "About to invoke callback for timer {Timer}"
    )]
    private static partial void LogTraceBeforeCallback(ILogger logger, GrainTimer timer);

    [LoggerMessage(
        EventId = (int)ErrorCode.TimerAfterCallback,
        Level = LogLevel.Trace,
        Message = "Completed timer callback for timer '{Timer}'."
    )]
    private static partial void LogTraceAfterCallback(ILogger logger, GrainTimer timer);

    [LoggerMessage(
        EventId = (int)ErrorCode.Timer_GrainTimerCallbackError,
        Level = LogLevel.Warning,
        Message = "Caught and ignored exception thrown from timer callback for timer '{Timer}'."
    )]
    private static partial void LogWarningCallbackException(ILogger logger, Exception exception, GrainTimer timer);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error cancelling timer callback."
    )]
    private static partial void LogErrorCancellingCallback(ILogger logger, Exception exception);
}

internal sealed class GrainTimer<T> : GrainTimer
{
    private readonly Func<T, CancellationToken, Task> _callback;
    private readonly T _state;

    public GrainTimer(
        TimerRegistry shared,
        IGrainContext grainContext,
        Func<T, CancellationToken, Task> callback,
        T state,
        bool interleave,
        bool keepAlive)
        : base(
            shared,
            grainContext,
            interleave,
            keepAlive)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _state = state;
    }

    protected override Task InvokeCallbackAsync(CancellationToken cancellationToken) => _callback(_state, cancellationToken);

    public override string ToString() => $"{base.ToString()} Callback: '{_callback?.Target}.{_callback?.Method}'. State: '{_state}'";
}

internal sealed class InterleavingGrainTimer : GrainTimer
{
    private readonly Func<object?, Task> _callback;
    private readonly object? _state;

    public InterleavingGrainTimer(
        TimerRegistry shared,
        IGrainContext grainContext,
        Func<object?, Task> callback,
        object? state)
        : base(
            shared,
            grainContext,
            interleave: true,
            keepAlive: false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _state = state;
    }

    protected override Task InvokeCallbackAsync(CancellationToken cancellationToken) => _callback(_state);

    public override string ToString() => $"{base.ToString()} Callback: '{_callback?.Target}.{_callback?.Method}'. State: '{_state}'";
}

// This interface exists for the IInvokable implementation, so that call filters behave as intended.
internal interface IGrainTimerInvoker : IAddressable
{
    /// <summary>
    /// Invokes the callback.
    /// </summary>
    Task InvokeCallbackAsync();
}
