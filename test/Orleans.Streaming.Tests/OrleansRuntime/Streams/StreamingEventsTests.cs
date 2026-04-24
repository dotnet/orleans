using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams
{
    public class StreamingEventsTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void StreamingEvents_EmitQueueChange_EmitsBalancerChangedOnly()
        {
            using var observer = new Observer(StreamingEvents.AllEvents);
            var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 13000), 1);
            var previousQueue = QueueId.GetQueueId("queue", 1, 1);
            var currentQueue = QueueId.GetQueueId("queue", 2, 2);
            var queueBalancer = new TestQueueBalancer();

            StreamingEvents.EmitQueueChange(
                "provider",
                siloAddress,
                [previousQueue],
                [currentQueue],
                queueBalancer);

            var changed = Assert.IsType<StreamingEvents.BalancerChanged>(Assert.Single(observer.Events));
            Assert.Equal("provider", changed.StreamProvider);
            Assert.Same(siloAddress, changed.SiloAddress);
            Assert.Equal(previousQueue, Assert.Single(changed.PreviousQueues));
            Assert.Equal(currentQueue, Assert.Single(changed.CurrentQueues));
            Assert.Same(queueBalancer, changed.QueueBalancer);
        }

        private sealed class TestQueueBalancer : IStreamQueueBalancer
        {
            public IEnumerable<QueueId> GetMyQueues() => [];
            public Task Initialize(IStreamQueueMapper queueMapper) => Task.CompletedTask;
            public Task Shutdown() => Task.CompletedTask;
            public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer) => true;
            public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer) => true;
        }

        private sealed class Observer : IObserver<StreamingEvents.StreamingEvent>, IDisposable
        {
            private readonly IDisposable _subscription;
            private readonly ConcurrentQueue<StreamingEvents.StreamingEvent> _events = new();

            public Observer(IObservable<StreamingEvents.StreamingEvent> observable)
            {
                _subscription = observable.Subscribe(this);
            }

            public StreamingEvents.StreamingEvent[] Events => _events.ToArray();

            public void Dispose() => _subscription.Dispose();

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(StreamingEvents.StreamingEvent value) => _events.Enqueue(value);
        }
    }
}
