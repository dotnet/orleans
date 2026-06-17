using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Core.Diagnostics;

internal static class GatewayEvents
{
    internal const string ListenerName = "Orleans.Gateway";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<GatewayEvent> AllEvents { get; } = new Observable();

    internal static bool IsClientDroppedEnabled() => Listener.IsEnabled(nameof(ClientDropped));

    internal static bool IsGatewayListUpdatedEnabled() => Listener.IsEnabled(nameof(GatewayListUpdated));

    internal abstract class GatewayEvent
    {
    }

    internal sealed class GatewayListUpdated(
        object source,
        IReadOnlyList<SiloAddress> knownGateways,
        IReadOnlyList<SiloAddress> liveGateways) : GatewayEvent
    {
        public readonly object Source = source;

        public readonly IReadOnlyList<SiloAddress> KnownGateways = knownGateways;

        public readonly IReadOnlyList<SiloAddress> LiveGateways = liveGateways;
    }

    internal sealed class ClientDropped(
        SiloAddress siloAddress,
        GrainId clientId,
        TimeSpan disconnectedDuration) : GatewayEvent
    {
        public readonly SiloAddress SiloAddress = siloAddress;

        public readonly GrainId ClientId = clientId;

        public readonly TimeSpan DisconnectedDuration = disconnectedDuration;
    }

    internal static void EmitGatewayListUpdated(object source, IReadOnlyList<SiloAddress> knownGateways, IReadOnlyList<SiloAddress> liveGateways)
    {
        if (!IsGatewayListUpdatedEnabled())
        {
            return;
        }

        Emit(source, knownGateways, liveGateways);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(object source, IReadOnlyList<SiloAddress> knownGateways, IReadOnlyList<SiloAddress> liveGateways)
        {
            Listener.Write(nameof(GatewayListUpdated), new GatewayListUpdated(source, knownGateways, liveGateways));
        }
    }

    internal static void EmitClientDropped(SiloAddress siloAddress, GrainId clientId, TimeSpan disconnectedDuration)
    {
        if (!IsClientDroppedEnabled())
        {
            return;
        }

        Emit(siloAddress, clientId, disconnectedDuration);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, GrainId clientId, TimeSpan disconnectedDuration)
        {
            Listener.Write(nameof(ClientDropped), new ClientDropped(siloAddress, clientId, disconnectedDuration));
        }
    }

    private sealed class Observable : IObservable<GatewayEvent>
    {
        public IDisposable Subscribe(IObserver<GatewayEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<GatewayEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is GatewayEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
