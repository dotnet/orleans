using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans activation rebalancer events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class ActivationRebalancerEvents
{
    /// <summary>
    /// The name of the diagnostic listener for activation rebalancer events.
    /// </summary>
    public const string ListenerName = "Orleans.ActivationRebalancer";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all activation rebalancer events.
    /// </summary>
    public static IObservable<RebalancerEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for activation rebalancer diagnostic events.
    /// </summary>
    public abstract class RebalancerEvent(SiloAddress siloAddress)
    {
        /// <summary>
        /// The address of the silo hosting the rebalancer.
        /// </summary>
        public readonly SiloAddress SiloAddress = siloAddress;
    }

    /// <summary>
    /// Event payload for when a rebalancing cycle starts.
    /// </summary>
    /// <param name="siloAddress">The address of the silo hosting the rebalancer.</param>
    /// <param name="cycleNumber">The cycle number within the current session.</param>
    public sealed class CycleStart(
        SiloAddress siloAddress,
        int cycleNumber) : RebalancerEvent(siloAddress)
    {
        /// <summary>
        /// The cycle number within the current session.
        /// </summary>
        public readonly int CycleNumber = cycleNumber;
    }

    /// <summary>
    /// Event payload for when a rebalancing cycle completes.
    /// </summary>
    /// <param name="siloAddress">The address of the silo hosting the rebalancer.</param>
    /// <param name="cycleNumber">The cycle number within the current session.</param>
    /// <param name="activationsMigrated">The number of activations migrated during the cycle.</param>
    /// <param name="entropyDeviation">The entropy deviation after the cycle.</param>
    /// <param name="elapsed">The time taken to complete the cycle.</param>
    /// <param name="sessionCompleted">Whether this cycle resulted in session completion.</param>
    public sealed class CycleStop(
        SiloAddress siloAddress,
        int cycleNumber,
        int activationsMigrated,
        double entropyDeviation,
        TimeSpan elapsed,
        bool sessionCompleted) : RebalancerEvent(siloAddress)
    {
        /// <summary>
        /// The cycle number within the current session.
        /// </summary>
        public readonly int CycleNumber = cycleNumber;

        /// <summary>
        /// The number of activations migrated during the cycle.
        /// </summary>
        public readonly int ActivationsMigrated = activationsMigrated;

        /// <summary>
        /// The entropy deviation after the cycle.
        /// </summary>
        public readonly double EntropyDeviation = entropyDeviation;

        /// <summary>
        /// The time taken to complete the cycle.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// Indicates whether this cycle resulted in session completion.
        /// </summary>
        public readonly bool SessionCompleted = sessionCompleted;
    }

    /// <summary>
    /// Event payload for when a rebalancing session starts.
    /// </summary>
    /// <param name="siloAddress">The address of the silo hosting the rebalancer.</param>
    public sealed class SessionStart(SiloAddress siloAddress) : RebalancerEvent(siloAddress)
    {
    }

    /// <summary>
    /// Event payload for when a rebalancing session stops.
    /// </summary>
    /// <param name="siloAddress">The address of the silo hosting the rebalancer.</param>
    /// <param name="reason">The reason the session stopped.</param>
    /// <param name="totalCycles">The total number of cycles completed in the session.</param>
    public sealed class SessionStop(
        SiloAddress siloAddress,
        string reason,
        int totalCycles) : RebalancerEvent(siloAddress)
    {
        /// <summary>
        /// The reason the session stopped.
        /// </summary>
        public readonly string Reason = reason;

        /// <summary>
        /// The total number of cycles completed in the session.
        /// </summary>
        public readonly int TotalCycles = totalCycles;
    }

    internal static void EmitCycleStart(SiloAddress siloAddress, int cycleNumber)
    {
        if (!Listener.IsEnabled(nameof(CycleStart)))
        {
            return;
        }

        Emit(siloAddress, cycleNumber);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, int cycleNumber)
        {
            Listener.Write(nameof(CycleStart), new CycleStart(
                siloAddress,
                cycleNumber));
        }
    }

    internal static void EmitCycleStop(SiloAddress siloAddress, int cycleNumber, int activationsMigrated, double entropyDeviation, TimeSpan elapsed, bool sessionCompleted)
    {
        if (!Listener.IsEnabled(nameof(CycleStop)))
        {
            return;
        }

        Emit(siloAddress, cycleNumber, activationsMigrated, entropyDeviation, elapsed, sessionCompleted);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, int cycleNumber, int activationsMigrated, double entropyDeviation, TimeSpan elapsed, bool sessionCompleted)
        {
            Listener.Write(nameof(CycleStop), new CycleStop(
                siloAddress,
                cycleNumber,
                activationsMigrated,
                entropyDeviation,
                elapsed,
                sessionCompleted));
        }
    }

    internal static void EmitSessionStart(SiloAddress siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SessionStart)))
        {
            return;
        }

        Emit(siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress)
        {
            Listener.Write(nameof(SessionStart), new SessionStart(siloAddress));
        }
    }

    internal static void EmitSessionStop(SiloAddress siloAddress, string reason, int totalCycles)
    {
        if (!Listener.IsEnabled(nameof(SessionStop)))
        {
            return;
        }

        Emit(siloAddress, reason, totalCycles);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, string reason, int totalCycles)
        {
            Listener.Write(nameof(SessionStop), new SessionStop(
                siloAddress,
                reason,
                totalCycles));
        }
    }

    private sealed class Observable : IObservable<RebalancerEvent>
    {
        public IDisposable Subscribe(IObserver<RebalancerEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<RebalancerEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is RebalancerEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
