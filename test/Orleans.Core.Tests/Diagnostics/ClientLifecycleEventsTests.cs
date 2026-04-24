using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Diagnostics;

public class ClientLifecycleEventsTests
{
    [Fact, TestCategory("BVT")]
    public async Task Lifecycle_EmitsObserverAndStageEvents()
    {
        var lifecycle = CreateLifecycle();
        var startObserver = new TestObserver();
        var activeObserver = new TestObserver();

        lifecycle.Subscribe("runtime", ServiceLifecycleStage.RuntimeInitialize, startObserver);
        lifecycle.Subscribe("active", ServiceLifecycleStage.Active, activeObserver);

        using var observer = new Observer(ClientLifecycleEvents.AllEvents);

        await lifecycle.OnStart();
        await lifecycle.OnStop();

        var events = observer.Events;

        Assert.Collection(
            events,
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStarting>(evt);
                Assert.Equal("runtime", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.Same(startObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverCompleted>(evt);
                Assert.Equal("runtime", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.Same(startObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.StageCompleted>(evt);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.Same(lifecycle.Lifecycle, typed.Lifecycle);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStarting>(evt);
                Assert.Equal("active", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.Active, typed.Stage);
                Assert.Equal("Active (20000)", typed.StageName);
                Assert.Same(activeObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverCompleted>(evt);
                Assert.Equal("active", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.Active, typed.Stage);
                Assert.Equal("Active (20000)", typed.StageName);
                Assert.Same(activeObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.StageCompleted>(evt);
                Assert.Equal(ServiceLifecycleStage.Active, typed.Stage);
                Assert.Equal("Active (20000)", typed.StageName);
                Assert.Same(lifecycle.Lifecycle, typed.Lifecycle);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStopping>(evt);
                Assert.Equal("active", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.Active, typed.Stage);
                Assert.Equal("Active (20000)", typed.StageName);
                Assert.Same(activeObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStopped>(evt);
                Assert.Equal("active", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.Active, typed.Stage);
                Assert.Equal("Active (20000)", typed.StageName);
                Assert.Same(activeObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.StageStopped>(evt);
                Assert.Equal(ServiceLifecycleStage.Active, typed.Stage);
                Assert.Equal("Active (20000)", typed.StageName);
                Assert.Same(lifecycle.Lifecycle, typed.Lifecycle);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStopping>(evt);
                Assert.Equal("runtime", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.Same(startObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStopped>(evt);
                Assert.Equal("runtime", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.Same(startObserver, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.StageStopped>(evt);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.Same(lifecycle.Lifecycle, typed.Lifecycle);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            });
    }

    [Fact, TestCategory("BVT")]
    public async Task Lifecycle_OnStartFailure_EmitsObserverFailed()
    {
        var lifecycle = CreateLifecycle();
        var observerInstance = new TestObserver(failOnStart: true);

        lifecycle.Subscribe("runtime", ServiceLifecycleStage.RuntimeInitialize, observerInstance);

        using var observer = new Observer(ClientLifecycleEvents.AllEvents);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.OnStart());

        var events = observer.Events;
        Assert.Collection(
            events,
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverStarting>(evt);
                Assert.Equal("runtime", typed.ObserverName);
                Assert.Same(observerInstance, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            },
            evt =>
            {
                var typed = Assert.IsType<ClientLifecycleEvents.ObserverFailed>(evt);
                Assert.Equal("runtime", typed.ObserverName);
                Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, typed.Stage);
                Assert.Equal("RuntimeInitialize (2000)", typed.StageName);
                Assert.IsType<InvalidOperationException>(typed.Exception);
                Assert.Same(observerInstance, typed.Observer);
                Assert.Same(lifecycle.ClientAddress, typed.ClientAddress);
            });
    }

    private static TestLifecycle CreateLifecycle()
    {
        var details = new LocalClientDetails(Options.Create(new ClientMessagingOptions
        {
            LocalAddress = IPAddress.Loopback,
        }));

        return new TestLifecycle(new ClusterClientLifecycle(NullLogger.Instance, details), details.ClientAddress);
    }

    private sealed class TestLifecycle(ClusterClientLifecycle lifecycle, SiloAddress clientAddress)
    {
        public ClusterClientLifecycle Lifecycle { get; } = lifecycle;
        public SiloAddress ClientAddress { get; } = clientAddress;

        public Task OnStart() => Lifecycle.OnStart();
        public Task OnStop() => Lifecycle.OnStop();
        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer) => Lifecycle.Subscribe(observerName, stage, observer);
    }

    private sealed class TestObserver(bool failOnStart = false, bool failOnStop = false) : ILifecycleObserver
    {
        public Task OnStart(CancellationToken cancellationToken)
        {
            if (failOnStart)
            {
                throw new InvalidOperationException("start failure");
            }

            return Task.CompletedTask;
        }

        public Task OnStop(CancellationToken cancellationToken)
        {
            if (failOnStop)
            {
                throw new InvalidOperationException("stop failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Observer : IObserver<ClientLifecycleEvents.LifecycleEvent>, IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly ConcurrentQueue<ClientLifecycleEvents.LifecycleEvent> _events = new();

        public Observer(IObservable<ClientLifecycleEvents.LifecycleEvent> observable)
        {
            _subscription = observable.Subscribe(this);
        }

        public ClientLifecycleEvents.LifecycleEvent[] Events => _events.ToArray();

        public void Dispose() => _subscription.Dispose();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ClientLifecycleEvents.LifecycleEvent value) => _events.Enqueue(value);
    }
}
