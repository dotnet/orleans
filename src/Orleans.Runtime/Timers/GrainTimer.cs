using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Runtime.Internal;
using Orleans.Serialization.Invocation;
using Orleans.Timers;
using Orleans.Runtime.Diagnostics;

namespace Orleans.Runtime;

internal abstract partial class GrainTimer : IGrainTimer
{
    protected static readonly GrainInterfaceType InvokableInterfaceType = GrainInterfaceType.Create("Orleans.Runtime.IGrainTimerInvoker");
    protected static readonly TimerCallback TimerCallback = (state) => ((GrainTimer)state!).ScheduleTickOnActivation();
    protected static readonly MethodInfo InvokableMethodInfo = typeof(IGrainTimerInvoker).GetMethod(nameof(IGrainTimerInvoker.InvokeCallbackAsync), BindingFlags.Instance | BindingFlags.Public)!;
    private readonly CancellationTokenSource _cts = new();
    private ITimer? _timer;
    private readonly IGrainContext _grainContext;
    private readonly TimerRegistry _shared;
    private readonly bool _interleave;
    private readonly bool _keepAlive;
    private readonly TimerTickInvoker _invoker;
    private bool _changed;
    private bool _firing;
    private bool _queued;
    private bool _pendingTick;
    private bool _scheduled;
    private bool _disposed;
    private long _scheduledAt;
    private TimeSpan _scheduledDueTime;
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
    }

    protected IGrainContext GrainContext => _grainContext;

    internal void Start(TimeSpan dueTime, TimeSpan period) => Change(dueTime, period);

    // Called with _cts locked. There is at most one queued invocation and one active callback.
    private bool ChangeTimer(TimeSpan dueTime)
    {
        _pendingTick = dueTime == TimeSpan.Zero;
        _scheduled = dueTime != TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan;
        if (_scheduled)
        {
            _scheduledAt = _shared.TimeProvider.GetTimestamp();
            // Match the millisecond resolution used by System.Threading.Timer.
            _scheduledDueTime = TimeSpan.FromMilliseconds((long)dueTime.TotalMilliseconds);
            if (_timer is null)
            {
                using (new ExecutionContextSuppressor())
                {
                    _timer = _shared.TimeProvider.CreateTimer(TimerCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }

            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        else
        {
            // Disarm an existing delayed timer, but never create one for an immediate or infinite arm.
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_pendingTick && !_queued)
            {
                _queued = true;
                return true;
            }
        }

        return false;
    }

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
        lock (_cts)
        {
            // A callback from a previous physical arm can arrive after Change or Dispose. Only the
            // current elapsed deadline is eligible, and changing to zero/infinite invalidates it.
            if (_disposed || !_scheduled || _firing
                || _shared.TimeProvider.GetElapsedTime(_scheduledAt) < _scheduledDueTime)
            {
                return;
            }

            _scheduled = false;
            _pendingTick = true;
            if (_queued)
            {
                return;
            }

            _queued = true;
        }

        QueueTickOnActivation();
    }

    // The admission is reserved under _cts, but delivered outside it: activation shutdown disposes
    // timers under its own lock. Taking that lock while holding _cts would invert the lock order.
    // Changes/disposal between reservation and delivery coalesce, invalidate, or cancel this tick.
    private void QueueTickOnActivation()
    {
        try
        {
            var msg = _shared.MessageFactory.CreateMessage(body: _invoker, options: InvokeMethodOptions.OneWay);
            // Timer requests start a new call chain, including ticks queued immediately during registration.
            msg.RequestContextData = null;
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
            lock (_cts)
            {
                _queued = false;
                _pendingTick = false;
            }

            try
            {
                LogErrorScheduleTickOnActivation(Logger, exception, this);
            }
            catch
            {
                // Allowing an exception to escape a physical timer callback would crash the process.
            }
        }
    }

    protected abstract Task InvokeCallbackAsync(CancellationToken cancellationToken);

    private ValueTask<Response> InvokeGrainTimerCallbackAsync()
    {
        lock (_cts)
        {
            _queued = false;
            if (!_pendingTick)
            {
                // Change to a delayed/infinite arm invalidated this queued tick. It must not invoke
                // user code, emit tick diagnostics, or replace the new schedule with the period.
                return new(Response.Completed);
            }

            _pendingTick = false;
            _firing = true;
            _changed = false;
        }

        try
        {
            LogTraceBeforeCallback(Logger, this);

            GrainTimerEvents.EmitTickStart(GrainContext, this);
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

                GrainTimerEvents.EmitTickStop(GrainContext, this);
                OnTickCompleted();
                return new(Response.Completed);
            }
        }
        catch (Exception exc)
        {
            GrainTimerEvents.EmitTickStop(GrainContext, this, exc);
            OnTickCompleted();
            return new(OnCallbackException(exc));
        }
    }

    private void OnTickCompleted()
    {
        bool queueTick;
        lock (_cts)
        {
            // Release the active callback before admitting another immediate tick. A Change while
            // the callback was running is coalesced, with the last change winning.
            _firing = false;
            if (_disposed)
            {
                return;
            }

            queueTick = ChangeTimer(_changed ? _dueTime : _period);
        }

        if (queueTick)
        {
            QueueTickOnActivation();
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

            GrainTimerEvents.EmitTickStop(GrainContext, this);

            return Response.Completed;
        }
        catch (Exception exc)
        {
            GrainTimerEvents.EmitTickStop(GrainContext, this, exc);

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

        var queueTick = false;
        lock (_cts)
        {
            if (_disposed)
            {
                return;
            }

            _changed = true;
            _dueTime = dueTime;
            _period = period;

            // Changes during an active callback take effect after completion. A queued message can
            // instead be coalesced or invalidated now, without admitting a second invocation.
            if (!_firing)
            {
                queueTick = ChangeTimer(dueTime);
            }
        }

        if (queueTick)
        {
            QueueTickOnActivation();
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
        lock (_cts)
        {
            if (_disposed)
            {
                return;
            }

            // Publish disposal before cancellation, whose registrations can reenter Change/Dispose.
            _disposed = true;
            _scheduled = false;
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            // Do not run cancellation registrations under the timer's state lock.
            _cts.Cancel();
        }
        catch (Exception exception)
        {
            LogErrorCancellingCallback(Logger, exception);
        }

        GrainTimerEvents.EmitDisposed(GrainContext, this);

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
        Task IGrainTimerInvoker.InvokeCallbackAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

        public int GetArgumentCount() => 0;

        public object? GetArgument(int index) => throw new InvalidOperationException();

        public void SetArgument(int index, object? value) => throw new InvalidOperationException();

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
    [Alias("3F6C2672")]
    Task InvokeCallbackAsync(CancellationToken cancellationToken = default);
}
