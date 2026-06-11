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

    internal abstract class GatewayEvent(SiloAddress siloAddress)
    {
        public readonly SiloAddress SiloAddress = siloAddress;
    }

    internal sealed class ClientDropped(
        SiloAddress siloAddress,
        GrainId clientId,
        TimeSpan disconnectedDuration) : GatewayEvent(siloAddress)
    {
        public readonly GrainId ClientId = clientId;

        public readonly TimeSpan DisconnectedDuration = disconnectedDuration;
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
