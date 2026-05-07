using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans grain timer events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class GrainTimerEvents
{
    /// <summary>
    /// The name of the diagnostic listener for grain timer events.
    /// </summary>
    public const string ListenerName = "Orleans.GrainTimers";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all timer events.
    /// </summary>
    public static IObservable<TimerEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for timer diagnostic events.
    /// </summary>
    public abstract class TimerEvent(IGrainContext grainContext, IGrainTimer timer)
    {
        /// <summary>
        /// The grain context that owns the timer.
        /// </summary>
        public readonly IGrainContext GrainContext = grainContext;

        /// <summary>
        /// The timer associated with the event.
        /// </summary>
        public readonly IGrainTimer Timer = timer;
    }

    /// <summary>
    /// Event payload for when a grain timer is created.
    /// </summary>
    /// <param name="grainContext">The grain context that owns the timer.</param>
    /// <param name="timer">The timer instance.</param>
    /// <param name="dueTime">The initial due time of the timer.</param>
    /// <param name="period">The timer period.</param>
    public sealed class Created(
        IGrainContext grainContext,
        IGrainTimer timer,
        TimeSpan dueTime,
        TimeSpan period) : TimerEvent(grainContext, timer)
    {
        /// <summary>
        /// The initial due time of the timer.
        /// </summary>
        public readonly TimeSpan DueTime = dueTime;

        /// <summary>
        /// The timer period.
        /// </summary>
        public readonly TimeSpan Period = period;
    }

    /// <summary>
    /// Event payload for when a grain timer tick callback is about to start.
    /// </summary>
    /// <param name="grainContext">The grain context that owns the timer.</param>
    /// <param name="timer">The timer instance.</param>
    public sealed class TickStart(
        IGrainContext grainContext,
        IGrainTimer timer) : TimerEvent(grainContext, timer)
    {
    }

    /// <summary>
    /// Event payload for when a grain timer tick callback has completed.
    /// </summary>
    /// <param name="grainContext">The grain context that owns the timer.</param>
    /// <param name="timer">The timer instance.</param>
    /// <param name="exception">The exception thrown by the callback, if any.</param>
    public sealed class TickStop(
        IGrainContext grainContext,
        IGrainTimer timer,
        Exception? exception) : TimerEvent(grainContext, timer)
    {
        /// <summary>
        /// The exception thrown by the callback, if any.
        /// </summary>
        public readonly Exception? Exception = exception;
    }

    /// <summary>
    /// Event payload for when a grain timer is disposed.
    /// </summary>
    /// <param name="grainContext">The grain context that owns the timer.</param>
    /// <param name="timer">The timer instance.</param>
    public sealed class Disposed(
        IGrainContext grainContext,
        IGrainTimer timer) : TimerEvent(grainContext, timer)
    {
    }

    internal static void EmitCreated(IGrainContext grainContext, TimeSpan dueTime, TimeSpan period, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(nameof(Created)))
        {
            return;
        }

        Emit(grainContext, dueTime, period, timer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TimeSpan dueTime, TimeSpan period, IGrainTimer timer)
        {
            Listener.Write(nameof(Created), new Created(
                grainContext,
                timer,
                dueTime,
                period));
        }
    }

    internal static void EmitDisposed(IGrainContext grainContext, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(nameof(Disposed)))
        {
            return;
        }

        Emit(grainContext, timer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, IGrainTimer timer)
        {
            Listener.Write(nameof(Disposed), new Disposed(
                grainContext,
                timer));
        }
    }

    internal static void EmitTickStart(IGrainContext grainContext, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(nameof(TickStart)))
        {
            return;
        }

        Emit(grainContext, timer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, IGrainTimer timer)
        {
            Listener.Write(nameof(TickStart), new TickStart(
                grainContext,
                timer));
        }
    }

    internal static void EmitTickStop(IGrainContext grainContext, IGrainTimer timer, Exception? exception = null)
    {
        if (!Listener.IsEnabled(nameof(TickStop)))
        {
            return;
        }

        Emit(grainContext, timer, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, IGrainTimer timer, Exception? exception)
        {
            Listener.Write(nameof(TickStop), new TickStop(
                grainContext,
                timer,
                exception));
        }
    }

    private sealed class Observable : IObservable<TimerEvent>
    {
        public IDisposable Subscribe(IObserver<TimerEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<TimerEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is TimerEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
