using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans grain activation events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class GrainLifecycleEvents
{
    /// <summary>
    /// The name of the diagnostic listener for grain activation events.
    /// </summary>
    public const string ListenerName = "Orleans.GrainsLifecycle";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all grain lifecycle events.
    /// </summary>
    public static IObservable<LifecycleEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for lifecycle diagnostic events.
    /// </summary>
    public abstract class LifecycleEvent(IGrainContext grainContext)
    {
        /// <summary>
        /// The grain context associated with the event.
        /// </summary>
        public readonly IGrainContext GrainContext = grainContext;
    }

    /// <summary>
    /// Event payload for when a grain activation is created.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    public sealed class Created(IGrainContext grainContext) : LifecycleEvent(grainContext)
    {
    }

    /// <summary>
    /// Event payload for when a grain activation has completed activation.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    public sealed class Activated(IGrainContext grainContext) : LifecycleEvent(grainContext)
    {
    }

    /// <summary>
    /// Event payload for when a grain activation is about to deactivate.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    /// <param name="reason">The reason for deactivation.</param>
    public sealed class Deactivating(
        IGrainContext grainContext,
        DeactivationReason reason) : LifecycleEvent(grainContext)
    {
        /// <summary>
        /// The reason for deactivation.
        /// </summary>
        public readonly DeactivationReason Reason = reason;
    }

    /// <summary>
    /// Event payload for when a grain activation has completed deactivation.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    /// <param name="reason">The reason for deactivation.</param>
    public sealed class Deactivated(
        IGrainContext grainContext,
        DeactivationReason reason) : LifecycleEvent(grainContext)
    {
        /// <summary>
        /// The reason for deactivation.
        /// </summary>
        public readonly DeactivationReason Reason = reason;
    }

    internal static void EmitActivated(IGrainContext grainContext)
    {
        if (!Listener.IsEnabled(nameof(Activated)))
        {
            return;
        }

        Emit(grainContext);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext)
        {
            Listener.Write(nameof(Activated), new Activated(grainContext));
        }
    }

    internal static void EmitCreated(IGrainContext grainContext)
    {
        if (!Listener.IsEnabled(nameof(Created)))
        {
            return;
        }

        Emit(grainContext);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext)
        {
            Listener.Write(nameof(Created), new Created(grainContext));
        }
    }

    internal static void EmitDeactivated(IGrainContext grainContext, DeactivationReason deactivationReason)
    {
        if (!Listener.IsEnabled(nameof(Deactivated)))
        {
            return;
        }

        Emit(grainContext, deactivationReason);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, DeactivationReason deactivationReason)
        {
            Listener.Write(nameof(Deactivated), new Deactivated(
                grainContext,
                deactivationReason));
        }
    }

    internal static void EmitDeactivating(IGrainContext grainContext, DeactivationReason deactivationReason)
    {
        if (!Listener.IsEnabled(nameof(Deactivating)))
        {
            return;
        }

        Emit(grainContext, deactivationReason);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, DeactivationReason deactivationReason)
        {
            Listener.Write(nameof(Deactivating), new Deactivating(
                grainContext,
                deactivationReason));
        }
    }

    private sealed class Observable : IObservable<LifecycleEvent>
    {
        public IDisposable Subscribe(IObserver<LifecycleEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<LifecycleEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is LifecycleEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
