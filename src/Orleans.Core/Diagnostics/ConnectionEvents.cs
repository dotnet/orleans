using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.Core.Diagnostics;

internal static class ConnectionEvents
{
    internal const string ListenerName = "Orleans.Connections";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<ConnectionEvent> AllEvents { get; } = new Observable();

    internal abstract class ConnectionEvent
    {
    }

    internal sealed class Connecting(SiloAddress endPoint) : ConnectionEvent
    {
        public readonly SiloAddress EndPoint = endPoint;
    }

    internal sealed class Connected(SiloAddress endPoint) : ConnectionEvent
    {
        public readonly SiloAddress EndPoint = endPoint;
    }

    internal sealed class Established(Connection connection, SiloAddress siloAddress) : ConnectionEvent
    {
        public readonly Connection Connection = connection;
        public readonly SiloAddress SiloAddress = siloAddress;
    }

    internal sealed class Terminated(Connection connection, Exception? exception) : ConnectionEvent
    {
        public readonly Connection Connection = connection;
        public readonly Exception? Exception = exception;
    }

    internal sealed class AcceptFailed(EndPoint endPoint, Exception exception) : ConnectionEvent
    {
        public readonly EndPoint EndPoint = endPoint;
        public readonly Exception Exception = exception;
    }

    internal static void EmitAcceptFailed(EndPoint endPoint, Exception exception)
    {
        if (!Listener.IsEnabled(nameof(AcceptFailed)))
        {
            return;
        }

        Emit(endPoint, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(EndPoint endPoint, Exception exception)
        {
            Listener.Write(nameof(AcceptFailed), new AcceptFailed(endPoint, exception));
        }
    }

    internal static void EmitConnected(SiloAddress endPoint)
    {
        if (!Listener.IsEnabled(nameof(Connected)))
        {
            return;
        }

        Emit(endPoint);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress endPoint)
        {
            Listener.Write(nameof(Connected), new Connected(endPoint));
        }
    }

    internal static void EmitConnecting(SiloAddress endPoint)
    {
        if (!Listener.IsEnabled(nameof(Connecting)))
        {
            return;
        }

        Emit(endPoint);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress endPoint)
        {
            Listener.Write(nameof(Connecting), new Connecting(endPoint));
        }
    }

    internal static void EmitEstablished(Connection connection, SiloAddress siloAddress)
    {
        if (!Listener.IsEnabled(nameof(Established)))
        {
            return;
        }

        Emit(connection, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Connection connection, SiloAddress siloAddress)
        {
            Listener.Write(nameof(Established), new Established(connection, siloAddress));
        }
    }

    internal static void EmitTerminated(Connection connection, Exception? exception)
    {
        if (!Listener.IsEnabled(nameof(Terminated)))
        {
            return;
        }

        Emit(connection, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(Connection connection, Exception? exception)
        {
            Listener.Write(nameof(Terminated), new Terminated(connection, exception));
        }
    }

    private sealed class Observable : IObservable<ConnectionEvent>
    {
        public IDisposable Subscribe(IObserver<ConnectionEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<ConnectionEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is ConnectionEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
