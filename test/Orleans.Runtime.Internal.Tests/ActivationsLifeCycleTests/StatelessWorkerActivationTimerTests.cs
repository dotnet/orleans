using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.Grains;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

// Keep lifecycle probes out of parallel test collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StatelessWorkerActivationTimerCollection
{
    public const string Name = "Stateless worker activation timers";
}

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
[Collection(StatelessWorkerActivationTimerCollection.Name)]
public sealed class StatelessWorkerActivationTimerTests
{
    [Fact]
    public async Task CompletedActivationsReleaseTheirTimeoutTimers()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var services = fixture.HostedCluster.Silos[0].ServiceProvider;
        var grains = services.GetRequiredService<IGrainFactory>();
        var tracker = services.GetRequiredService<StatelessWorkerSingleActivationTracker>();
        using var events = new DiagnosticEventCollector(ActivationTimeoutEvents.ListenerName);
        var stable = grains.GetGrain<IStatelessWorkerSingleActivationGrain>(0);
        await stable.DoWork().WaitAsync(token);
        AssertTimeoutReleased(stable.GetGrainId());

        // Unrelated process timers must not affect the activation resource check.
        using var unrelatedTimer = new Timer(_ => { }, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
        for (var call = 0; call < 100; ++call)
        {
            await stable.DoWork().WaitAsync(token);
            AssertTimeoutReleased(stable.GetGrainId());
        }
        Assert.Equal(1, tracker.Current);

        for (var key = 1; key <= 100; ++key)
        {
            var grain = grains.GetGrain<IStatelessWorkerSingleActivationGrain>(key);
            await grain.DoWork().WaitAsync(token);
            AssertTimeoutReleased(grain.GetGrainId());
        }
        Assert.Equal(101, tracker.Current);

        void AssertTimeoutReleased(GrainId grainId)
        {
            var scheduled = Assert.Single(events.GetEvents(nameof(ActivationTimeoutEvents.Scheduled))
                .Select(item => Assert.IsType<ActivationTimeoutEvents.Scheduled>(item.Payload)),
                item => item.GrainContext.GrainId == grainId);
            // The wrapper delegates the deadline to ActivationData instead of allocating
            // another timed caller source. The command disposes its sole timeout source.
            Assert.False(scheduled.CallerToken.CanBeCanceled);
            Assert.True(scheduled.TimeoutToken.CanBeCanceled);
            Assert.Throws<ObjectDisposedException>(() => scheduled.TimeoutToken.WaitHandle);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingTimeoutDiagnosticsDoNotInterruptActivation(bool throwFromPredicate)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        using var listener = new ThrowingTimeoutListener(throwFromPredicate);
        var grain = fixture.GrainFactory.GetGrain<IStatelessWorkerSingleActivationGrain>(Guid.NewGuid().GetHashCode());
        var token = TestContext.Current.CancellationToken;
        await grain.DoWork().WaitAsync(TimeSpan.FromSeconds(10), token);
        await grain.DoWork().WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.True(listener.Failures > 0);
        Assert.Equal(1, fixture.HostedCluster.Silos[0].ServiceProvider
            .GetRequiredService<StatelessWorkerSingleActivationTracker>().Current);
        if (!throwFromPredicate)
        {
            var scheduled = Assert.IsType<ActivationTimeoutEvents.Scheduled>(listener.Payload);
            Assert.Throws<ObjectDisposedException>(() => scheduled.TimeoutToken.WaitHandle);
        }
    }

    private sealed class ThrowingTimeoutListener : IObserver<System.Diagnostics.DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly bool _throwFromPredicate;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _allListeners;
        private int _failures;
        public int Failures => Volatile.Read(ref _failures);
        public object? Payload { get; private set; }

        public ThrowingTimeoutListener(bool throwFromPredicate)
        {
            _throwFromPredicate = throwFromPredicate;
            _allListeners = System.Diagnostics.DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(System.Diagnostics.DiagnosticListener listener)
        {
            if (listener.Name == ActivationTimeoutEvents.ListenerName)
            {
                _subscriptions.Add(listener.Subscribe(this, _ =>
                {
                    if (_throwFromPredicate)
                    {
                        Interlocked.Increment(ref _failures);
                        throw new InvalidOperationException("Diagnostic predicate failed.");
                    }
                    return true;
                }));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            Payload = value.Value;
            Interlocked.Increment(ref _failures);
            throw new InvalidOperationException("Diagnostic observer failed.");
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            _allListeners.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }

    [Fact]
    public async Task ActivationTimeoutCancelsTheCallbackAndAwaitsItsCompletion()
    {
        await using var isolated = new Fixture { ActivationTimeout = TimeSpan.FromSeconds(1) };
        await isolated.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var registry = isolated.HostedCluster.Silos[0].ServiceProvider.GetRequiredService<LifecycleCallbackRegistry>();
        var grain = isolated.GrainFactory.GetGrain<IStatelessActivationTimeoutGrain>(
            $"{LifecycleCallback.OnActivateAsync}/{Guid.NewGuid():N}");
        var id = grain.GetGrainId();
        registry.Track(id);
        var call = grain.Ping();
        var probe = await registry.Get(id).Probes.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await probe.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.False(call.IsCompleted);
            Assert.False(probe.ScopeDisposed);
            Assert.False(probe.Context.Deactivated.IsCompleted);
            Assert.Equal(1, registry.Get(id).ActivationCount);
        }
        finally
        {
            probe.Release.TrySetResult();
        }
        await probe.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.True(probe.ScopeDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(30), token));
        await grain.Ping().WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.Equal(2, registry.Get(id).ActivationCount);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromMinutes(5);

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            // Keep background maintenance independent of the activation timeout checks.
            var clock = new FakeTimeProvider();
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.UseTimeProviderForBackgroundAreas(clock);
                silo.Services.AddSingleton<StatelessWorkerSingleActivationTracker>();
                silo.Services.AddSingleton<LifecycleCallbackRegistry>();
                silo.Services.AddScoped<LifecycleCallbackProbe>();
                silo.Services.Configure<StatelessWorkerOptions>(options => options.RemoveIdleWorkers = false);
                silo.Services.Configure<GrainCollectionOptions>(options => options.ActivationTimeout = ActivationTimeout);
            });
            builder.ConfigureClient(client => client.Services.UseTimeProviderForBackgroundAreas(clock));
        }

        public override async ValueTask DisposeAsync()
        {
            HostedCluster.Silos[0].ServiceProvider.GetRequiredService<LifecycleCallbackRegistry>().ReleaseAll();
            await base.DisposeAsync();
        }
    }
}

public interface IStatelessActivationTimeoutGrain : IGrainWithStringKey
{
    Task Ping();
}

[StatelessWorker(1)]
public sealed class StatelessActivationTimeoutGrain(LifecycleCallbackProbe probe) : Grain, IStatelessActivationTimeoutGrain
{
    public Task Ping() => Task.CompletedTask;

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        probe.Run(LifecycleCallback.OnActivateAsync, cancellationToken);
}
