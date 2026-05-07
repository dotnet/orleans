using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using Orleans.Runtime;
using Xunit;
using Orleans.Core.Diagnostics;

namespace Tester.Diagnostics;

public class ConnectionEventsTests
{
    [Fact, TestCategory("BVT")]
    public void ConnectionEvents_EmitConnecting_EmitsConnecting()
    {
        using var observer = new Observer(ConnectionEvents.AllEvents);
        var endPoint = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);

        ConnectionEvents.EmitConnecting(endPoint);

        var connecting = Assert.Single(observer.Events.OfType<ConnectionEvents.Connecting>(), evt => ReferenceEquals(evt.EndPoint, endPoint));
        Assert.Same(endPoint, connecting.EndPoint);
    }

    [Fact, TestCategory("BVT")]
    public void ConnectionEvents_EmitAcceptFailed_EmitsAcceptFailed()
    {
        using var observer = new Observer(ConnectionEvents.AllEvents);
        EndPoint endPoint = new IPEndPoint(IPAddress.Loopback, 13001);
        var exception = new InvalidOperationException("boom");

        ConnectionEvents.EmitAcceptFailed(endPoint, exception);

        var failed = Assert.Single(observer.Events.OfType<ConnectionEvents.AcceptFailed>(), evt => ReferenceEquals(evt.Exception, exception));
        Assert.Same(endPoint, failed.EndPoint);
    }

    private sealed class Observer : IObserver<ConnectionEvents.ConnectionEvent>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<ConnectionEvents.ConnectionEvent> _events = new();

        public Observer(IObservable<ConnectionEvents.ConnectionEvent> observable)
        {
            _subscription = observable.Subscribe(this);
        }

        public ConnectionEvents.ConnectionEvent[] Events => _events.ToArray();

        public void Dispose() => _subscription.Dispose();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ConnectionEvents.ConnectionEvent value) => _events.Enqueue(value);
    }
}
