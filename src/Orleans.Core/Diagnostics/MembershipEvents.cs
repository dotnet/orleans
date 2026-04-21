using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Core.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans membership events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
internal static class MembershipEvents
{
    /// <summary>
    /// The name of the diagnostic listener for membership events.
    /// </summary>
    public const string ListenerName = "Orleans.Membership";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all membership events.
    /// </summary>
    public static IObservable<MembershipEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for membership diagnostic events.
    /// </summary>
    public abstract class MembershipEvent
    {
    }

    /// <summary>
    /// Event payload for when the membership view changes.
    /// </summary>
    /// <param name="snapshot">The new membership snapshot.</param>
    /// <param name="observerSiloAddress">The address of the silo that observed this change.</param>
    public sealed class ViewChanged(
        MembershipTableSnapshot snapshot,
        SiloAddress? observerSiloAddress) : MembershipEvent
    {
        /// <summary>
        /// The new membership snapshot.
        /// </summary>
        public readonly MembershipTableSnapshot Snapshot = snapshot;

        /// <summary>
        /// The address of the silo that observed this change.
        /// </summary>
        public readonly SiloAddress? ObserverSiloAddress = observerSiloAddress;
    }

    internal static void EmitViewChanged(MembershipTableSnapshot newSnapshot, SiloAddress observerAddress)
    {
        if (!Listener.IsEnabled(nameof(ViewChanged)))
        {
            return;
        }

        Emit(newSnapshot, observerAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(MembershipTableSnapshot newSnapshot, SiloAddress observerAddress)
        {
            Listener.Write(nameof(ViewChanged), new ViewChanged(
                newSnapshot,
                observerAddress));
        }
    }

    private sealed class Observable : IObservable<MembershipEvent>
    {
        public IDisposable Subscribe(IObserver<MembershipEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<MembershipEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is MembershipEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
