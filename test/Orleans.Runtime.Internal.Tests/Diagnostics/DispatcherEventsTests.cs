using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Xunit;
using Orleans.Runtime.Diagnostics;

namespace UnitTests.Diagnostics;

[Collection(TestEnvironmentFixture.DefaultCollection)]
public class DispatcherEventsTests
{
    [Fact, TestCategory("BVT")]
    public void RuntimeMessagingTrace_OnDispatcherRejectMessage_EmitsRejected()
    {
        using var observer = new Observer(DispatcherEvents.AllEvents);
        var trace = new RuntimeMessagingTrace(NullLoggerFactory.Instance);
        var message = new Message();
        var exception = new InvalidOperationException("boom");

        trace.OnDispatcherRejectMessage(message, Message.RejectionTypes.Transient, "reason", exception);

        var rejected = Assert.Single(observer.Events.OfType<DispatcherEvents.Rejected>(), evt => ReferenceEquals(evt.Message, message));
        Assert.Equal(Message.RejectionTypes.Transient, rejected.RejectionType);
        Assert.Equal("reason", rejected.Reason);
        Assert.Same(exception, rejected.Exception);
    }

    [Fact, TestCategory("BVT")]
    public void RuntimeMessagingTrace_OnDispatcherForwardingMultiple_EmitsForwardingMultiple()
    {
        using var observer = new Observer(DispatcherEvents.AllEvents);
        var trace = new RuntimeMessagingTrace(NullLoggerFactory.Instance);
        var oldAddress = new GrainAddress
        {
            GrainId = GrainId.Create("test", "grain"),
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12000), 1),
        };
        var forwardingAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12001), 2);
        var exception = new InvalidOperationException("boom");

        trace.OnDispatcherForwardingMultiple(3, oldAddress, forwardingAddress, "test", exception);

        var forwarding = Assert.Single(observer.Events.OfType<DispatcherEvents.ForwardingMultiple>(), evt => evt.MessageCount == 3);
        Assert.Same(oldAddress, forwarding.OldAddress);
        Assert.Same(forwardingAddress, forwarding.ForwardingAddress);
        Assert.Equal("test", forwarding.FailedOperation);
        Assert.Same(exception, forwarding.Exception);
    }

    private sealed class Observer : IObserver<DispatcherEvents.DispatcherEvent>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<DispatcherEvents.DispatcherEvent> _events = new();

        public Observer(IObservable<DispatcherEvents.DispatcherEvent> observable)
        {
            _subscription = observable.Subscribe(this);
        }

        public DispatcherEvents.DispatcherEvent[] Events => _events.ToArray();

        public void Dispose() => _subscription.Dispose();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(DispatcherEvents.DispatcherEvent value) => _events.Enqueue(value);
    }
}
