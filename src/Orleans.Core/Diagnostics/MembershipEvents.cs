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

    /// <summary>
    /// The type of suspect or kill request.
    /// </summary>
    public enum SuspectOrKillRequestType
    {
        /// <summary>
        /// Unknown request type.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// A request to suspect a silo or declare it dead.
        /// </summary>
        SuspectOrKill,

        /// <summary>
        /// A request to declare a silo dead.
        /// </summary>
        Kill
    }

    /// <summary>
    /// Event payload for when a suspect or kill request completes.
    /// </summary>
    /// <param name="observerSiloAddress">The silo which processed the request.</param>
    /// <param name="siloAddress">The silo targeted by the request.</param>
    /// <param name="otherSiloAddress">The other silo associated with the request, if any.</param>
    /// <param name="requestType">The request type.</param>
    /// <param name="success">Whether the request completed successfully.</param>
    /// <param name="exception">The exception thrown by the request, if any.</param>
    public sealed class SuspectOrKillRequestCompleted(
        SiloAddress observerSiloAddress,
        SiloAddress siloAddress,
        SiloAddress? otherSiloAddress,
        SuspectOrKillRequestType requestType,
        bool success,
        Exception? exception) : MembershipEvent
    {
        /// <summary>
        /// The silo which processed the request.
        /// </summary>
        public readonly SiloAddress ObserverSiloAddress = observerSiloAddress;

        /// <summary>
        /// The silo targeted by the request.
        /// </summary>
        public readonly SiloAddress SiloAddress = siloAddress;

        /// <summary>
        /// The other silo associated with the request, if any.
        /// </summary>
        public readonly SiloAddress? OtherSiloAddress = otherSiloAddress;

        /// <summary>
        /// The request type.
        /// </summary>
        public readonly SuspectOrKillRequestType RequestType = requestType;

        /// <summary>
        /// Whether the request completed successfully.
        /// </summary>
        public readonly bool Success = success;

        /// <summary>
        /// The exception thrown by the request, if any.
        /// </summary>
        public readonly Exception? Exception = exception;
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

    internal static void EmitSuspectOrKillRequestCompleted(
        SiloAddress observerSiloAddress,
        SiloAddress siloAddress,
        SiloAddress? otherSiloAddress,
        SuspectOrKillRequestType requestType,
        bool success,
        Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(SuspectOrKillRequestCompleted)))
        {
            return;
        }

        Emit(observerSiloAddress, siloAddress, otherSiloAddress, requestType, success, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            SiloAddress observerSiloAddress,
            SiloAddress siloAddress,
            SiloAddress? otherSiloAddress,
            SuspectOrKillRequestType requestType,
            bool success,
            Exception? exception)
        {
            Listener.Write(nameof(SuspectOrKillRequestCompleted), new SuspectOrKillRequestCompleted(
                observerSiloAddress,
                siloAddress,
                otherSiloAddress,
                requestType,
                success,
                exception));
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
