using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for deployment load publisher statistics events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class DeploymentLoadPublisherEvents
{
    /// <summary>
    /// The name of the diagnostic listener for deployment load publisher statistics events.
    /// </summary>
    public const string ListenerName = "Orleans.DeploymentLoadPublisher";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all deployment load publisher events.
    /// </summary>
    public static IObservable<DeploymentLoadPublisherEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for deployment load publisher diagnostic events.
    /// </summary>
    public abstract class DeploymentLoadPublisherEvent
    {
    }

    /// <summary>
    /// Event payload for when a silo publishes its runtime statistics to the cluster.
    /// </summary>
    /// <param name="siloAddress">The address of the silo publishing statistics.</param>
    /// <param name="statistics">The published runtime statistics.</param>
    public sealed class Published(
        SiloAddress siloAddress,
        SiloRuntimeStatistics statistics) : DeploymentLoadPublisherEvent
    {
        /// <summary>
        /// The address of the silo publishing statistics.
        /// </summary>
        public readonly SiloAddress SiloAddress = siloAddress;

        /// <summary>
        /// The published runtime statistics.
        /// </summary>
        public readonly SiloRuntimeStatistics Statistics = statistics;
    }

    /// <summary>
    /// Event payload for when a silo receives runtime statistics from another silo.
    /// </summary>
    /// <param name="fromSilo">The address of the silo that sent the statistics.</param>
    /// <param name="receiverSilo">The address of the silo that received the statistics.</param>
    /// <param name="statistics">The received runtime statistics.</param>
    public sealed class Received(
        SiloAddress fromSilo,
        SiloAddress receiverSilo,
        SiloRuntimeStatistics statistics) : DeploymentLoadPublisherEvent
    {
        /// <summary>
        /// The address of the silo that sent the statistics.
        /// </summary>
        public readonly SiloAddress FromSilo = fromSilo;

        /// <summary>
        /// The address of the silo that received the statistics.
        /// </summary>
        public readonly SiloAddress ReceiverSilo = receiverSilo;

        /// <summary>
        /// The received runtime statistics.
        /// </summary>
        public readonly SiloRuntimeStatistics Statistics = statistics;
    }

    /// <summary>
    /// Event payload for when a silo completes refreshing statistics from all cluster members.
    /// </summary>
    /// <param name="siloAddress">The address of the silo that completed the refresh.</param>
    /// <param name="statistics">The current cached cluster statistics.</param>
    public sealed class ClusterRefreshed(
        SiloAddress siloAddress,
        IReadOnlyDictionary<SiloAddress, SiloRuntimeStatistics> statistics) : DeploymentLoadPublisherEvent
    {
        /// <summary>
        /// The address of the silo that completed the refresh.
        /// </summary>
        public readonly SiloAddress SiloAddress = siloAddress;

        /// <summary>
        /// The current cached cluster statistics.
        /// </summary>
        public readonly IReadOnlyDictionary<SiloAddress, SiloRuntimeStatistics> Statistics = statistics;
    }

    /// <summary>
    /// Event payload for when a silo's statistics are removed from the cache.
    /// </summary>
    /// <param name="removedSilo">The address of the silo whose statistics were removed.</param>
    /// <param name="observerSilo">The address of the silo that removed the statistics.</param>
    public sealed class Removed(
        SiloAddress removedSilo,
        SiloAddress observerSilo) : DeploymentLoadPublisherEvent
    {
        /// <summary>
        /// The address of the silo whose statistics were removed.
        /// </summary>
        public readonly SiloAddress RemovedSilo = removedSilo;

        /// <summary>
        /// The address of the silo that removed the statistics.
        /// </summary>
        public readonly SiloAddress ObserverSilo = observerSilo;
    }

    internal static void EmitClusterRefreshed(SiloAddress siloAddress, IReadOnlyDictionary<SiloAddress, SiloRuntimeStatistics> statistics)
    {
        if (!Listener.IsEnabled(nameof(ClusterRefreshed)))
        {
            return;
        }

        Emit(siloAddress, statistics);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, IReadOnlyDictionary<SiloAddress, SiloRuntimeStatistics> statistics)
        {
            Listener.Write(nameof(ClusterRefreshed), new ClusterRefreshed(
                siloAddress,
                statistics));
        }
    }

    internal static void EmitPublished(SiloAddress siloAddress, SiloRuntimeStatistics statistics)
    {
        if (!Listener.IsEnabled(nameof(Published)))
        {
            return;
        }

        Emit(siloAddress, statistics);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, SiloRuntimeStatistics statistics)
        {
            Listener.Write(nameof(Published), new Published(
                siloAddress,
                statistics));
        }
    }

    internal static void EmitReceived(SiloAddress sourceSiloAddress, SiloAddress observerSiloAddress, SiloRuntimeStatistics statistics)
    {
        if (sourceSiloAddress == observerSiloAddress
            || !Listener.IsEnabled(nameof(Received)))
        {
            return;
        }

        Emit(sourceSiloAddress, observerSiloAddress, statistics);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress sourceSiloAddress, SiloAddress observerSiloAddress, SiloRuntimeStatistics statistics)
        {
            Listener.Write(nameof(Received), new Received(
                sourceSiloAddress,
                observerSiloAddress,
                statistics));
        }
    }

    internal static void EmitRemoved(SiloAddress removedSiloAddress, SiloAddress observerSiloAddress)
    {
        if (!Listener.IsEnabled(nameof(Removed)))
        {
            return;
        }

        Emit(removedSiloAddress, observerSiloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress removedSiloAddress, SiloAddress observerSiloAddress)
        {
            Listener.Write(nameof(Removed), new Removed(
                removedSiloAddress,
                observerSiloAddress));
        }
    }

    private sealed class Observable : IObservable<DeploymentLoadPublisherEvent>
    {
        public IDisposable Subscribe(IObserver<DeploymentLoadPublisherEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<DeploymentLoadPublisherEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is DeploymentLoadPublisherEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
