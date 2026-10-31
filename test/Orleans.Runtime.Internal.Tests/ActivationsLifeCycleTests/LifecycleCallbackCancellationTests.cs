using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Verifies that the runtime cancels the token passed to activation and deactivation callbacks, then awaits those
/// callbacks before it unregisters the activation, disposes its services, or creates a replacement activation.
/// </summary>
[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public class LifecycleCallbackCancellationTests(LifecycleCallbackCancellationTests.Fixture fixture) : IClassFixture<LifecycleCallbackCancellationTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(LifecycleCallback.OnActivateAsync, false)]
    [InlineData(LifecycleCallback.OnActivateAsync, true)]
    [InlineData(LifecycleCallback.LifecycleStart, false)]
    public async Task CanceledActivation_AwaitsCallbackBeforeReleasingActivation(LifecycleCallback callback, bool cancelCallback)
    {
        var grain = fixture.GetGrain(callback);
        var call = grain.Ping();
        var probe = await fixture.ReadProbe(grain);
        await probe.Entered.Task.WaitAsync(Timeout, Cancellation);
        probe.CancelCallback = cancelCallback;

        // Deactivating during activation cancels the activation token, which is the token canceled by ActivationTimeout.
        probe.Context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Canceled during activation."), Cancellation);
        await AssertCallbackAwaitedPastCancellation(probe, call);

        await call.WaitAsync(Timeout, Cancellation);
        Assert.True(probe.ScopeDisposed);
        var activationCompleted = callback == LifecycleCallback.OnActivateAsync && !cancelCallback;
        Assert.Equal(activationCompleted ? 1 : 0, probe.DeactivationCalls);
        Assert.Equal(
            activationCompleted
                ? ["Created", "Deactivating", "CallbackCompleted", "OnDeactivateAsync", "Deactivated", "Created", "Activated"]
                : new[] { "Created", "Deactivating", "CallbackCompleted", "Deactivated", "Created", "Activated" },
            fixture.GetTimeline(grain));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationTimeout_UsesCallbackOutcome(bool cancelCallback)
    {
        var grain = fixture.GetGrain(LifecycleCallback.OnActivateAsync);
        var services = fixture.HostedCluster.Silos[0].ServiceProvider;
        var activation = Assert.IsType<ActivationData>(services.GetRequiredService<GrainContextActivator>().CreateInstance(
            new GrainAddress
            {
                GrainId = grain.GetGrainId(),
                ActivationId = ActivationId.NewId(),
                SiloAddress = fixture.HostedCluster.Silos[0].SiloAddress,
            }));
        using var cancellation = new CancellationTokenSource();
        activation.Activate(null, cancellation.Token);
        var probe = await fixture.ReadProbe(grain);
        try
        {
            await probe.Entered.Task.WaitAsync(Timeout, Cancellation);
            probe.CancelCallback = cancelCallback;

            // The activation timeout cancels the activation token without requesting deactivation.
            using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
            var activated = cancelCallback
                ? null
                : events.WaitForEventAsync(
                    nameof(GrainLifecycleEvents.Activated),
                    evt => evt.Payload is GrainLifecycleEvents.Activated a && ReferenceEquals(a.GrainContext, activation),
                    Timeout,
                    Cancellation);
            cancellation.Cancel();
            await probe.Canceled.Task.WaitAsync(Timeout, Cancellation);
            Assert.False(probe.ScopeDisposed);
            Assert.False(activation.Deactivated.IsCompleted);
            probe.Release.TrySetResult();

            if (activated is null)
            {
                await activation.Deactivated.WaitAsync(Timeout, Cancellation);
                Assert.Equal(["Created", "CallbackCompleted", "Deactivating", "Deactivated"], fixture.GetTimeline(grain));
            }
            else
            {
                await activated;
                Assert.Equal(ActivationState.Active, activation.State);
                Assert.Equal(0, probe.DeactivationCalls);
                activation.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Deactivate a timed-out activation."), Cancellation);
                await activation.Deactivated.WaitAsync(Timeout, Cancellation);
                Assert.Equal(
                    ["Created", "CallbackCompleted", "Activated", "Deactivating", "OnDeactivateAsync", "Deactivated"],
                    fixture.GetTimeline(grain));
            }

            Assert.True(probe.ScopeDisposed);
            Assert.Equal(cancelCallback ? 0 : 1, probe.DeactivationCalls);
        }
        finally
        {
            probe.Release.TrySetResult();
            activation.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Test cleanup."), Cancellation);
            await activation.Deactivated.WaitAsync(Timeout, Cancellation);
        }
    }

    [Fact]
    public async Task DeactivationTimeout_StartsWhenDeactivationCallbacksCanRun()
    {
        var deactivationTimeout = TimeSpan.FromSeconds(1);
        await using var isolated = new Fixture { ConfiguredDeactivationTimeout = deactivationTimeout };
        await isolated.InitializeAsync();
        var grain = isolated.GetGrain(LifecycleCallback.OnActivateAsync);
        var call = grain.Ping();
        var probe = await isolated.ReadProbe(grain);
        await probe.Entered.Task.WaitAsync(Timeout, Cancellation);
        probe.Context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Deactivation requested during activation."), Cancellation);
        await probe.Canceled.Task.WaitAsync(Timeout, Cancellation);

        // The runtime timer uses wall-clock time, so activation deliberately outlasts the deactivation timeout measured from the request.
        await Task.Delay(deactivationTimeout * 2, Cancellation);
        probe.Release.SetResult();
        await probe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
        await call.WaitAsync(Timeout, Cancellation);

        Assert.Equal(1, probe.DeactivationCalls);
        Assert.False(probe.DeactivationTokenCanceledOnEntry);
    }

    [Theory]
    [InlineData(LifecycleCallback.OnDeactivateAsync)]
    [InlineData(LifecycleCallback.LifecycleStop)]
    public async Task DeactivationTimeout_AwaitsCallbackBeforeReleasingActivation(LifecycleCallback callback)
    {
        var grain = fixture.GetGrain(callback);
        await grain.Ping().WaitAsync(Timeout, Cancellation);
        var probe = await fixture.ReadProbe(grain);

        // The fixture's DeactivationTimeout elapses while the callback waits for release.
        probe.Context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Deactivation timeout test."), Cancellation);
        await probe.Entered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.Ping();
        await AssertCallbackAwaitedPastCancellation(probe, call);

        await call.WaitAsync(Timeout, Cancellation);
        Assert.True(probe.ScopeDisposed);
        Assert.Equal(
            ["Created", "Activated", "Deactivating", "OnDeactivateAsync", "CallbackCompleted", "Deactivated", "Created", "Activated"],
            fixture.GetTimeline(grain));
    }

    [Theory]
    [InlineData(false, LifecycleCallback.OnDeactivateAsync)]
    [InlineData(true, LifecycleCallback.LifecycleStop)]
    public async Task CollectionTimeout_AllowsLaterPassWhileCallbackRuns(bool memoryPressure, LifecycleCallback callback)
    {
        await using var isolated = new Fixture();
        await isolated.InitializeAsync();
        using var events = new DiagnosticEventCollector(ActivationCollectionEvents.ListenerName);
        var collector = isolated.HostedCluster.Silos[0].ServiceProvider.GetRequiredService<ActivationCollector>();
        var grain = isolated.GetGrain(callback);
        await grain.Ping().WaitAsync(Timeout, Cancellation);
        var probe = await isolated.ReadProbe(grain);
        var completed = WaitForCollection(grain.GetGrainId());
        var collection = Collect();
        try
        {
            await probe.Entered.Task.WaitAsync(Timeout, Cancellation);
            await probe.Canceled.Task.WaitAsync(Timeout, Cancellation);
            isolated.CollectionTime.Advance(Fixture.DeactivationTimeout);
            await collection.WaitAsync(Timeout, Cancellation);

            Assert.False(probe.ScopeDisposed);
            Assert.False(probe.Context.Deactivated.IsCompleted);
            Assert.True(isolated.HostedCluster.TryGetGrainContext(grain.GetGrainId(), out var current));
            Assert.Same(probe.Context, current);
            Assert.False(completed.IsCompleted);

            var other = isolated.GetGrain(LifecycleCallback.None);
            await other.Ping().WaitAsync(Timeout, Cancellation);
            var otherProbe = await isolated.ReadProbe(other);
            var otherCompleted = WaitForCollection(other.GetGrainId());
            await Collect().WaitAsync(Timeout, Cancellation);
            await otherProbe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
            await otherCompleted;
            Assert.True(otherProbe.ScopeDisposed);
            Assert.False(probe.ScopeDisposed);
            Assert.False(completed.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
            await collection.WaitAsync(Timeout, Cancellation);
            await probe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
        }

        await completed;
        Assert.True(probe.ScopeDisposed);

        Task Collect() => memoryPressure
            ? collector.DeactivateInDueTimeOrder(1, Cancellation)
            : collector.CollectActivations(TimeSpan.Zero, Cancellation);

        Task WaitForCollection(GrainId grainId) => events.WaitForEventAsync(
            nameof(ActivationCollectionEvents.CollectionCompleted),
            evt => evt.Payload is ActivationCollectionEvents.CollectionCompleted batch
                && batch.Activations.Any(activation => activation.GrainId == grainId),
            Timeout,
            Cancellation);
    }

    private static async Task AssertCallbackAwaitedPastCancellation(LifecycleCallbackProbe probe, Task call)
    {
        await probe.Canceled.Task.WaitAsync(Timeout, Cancellation);
        Assert.False(call.IsCompleted);

        // Cancellation has already queued any runtime continuation on the activation scheduler, so release runs after it.
        probe.Context.Scheduler.QueueAction(() => probe.Release.SetResult());
        var observation = await probe.Observation.Task.WaitAsync(Timeout, Cancellation);
        Assert.Equal(new CallbackObservation(ScopeDisposed: false, IsMessageTarget: true, Deactivated: false, ActivationCount: 1), observation);

        await probe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        public static readonly TimeSpan DeactivationTimeout = TimeSpan.FromMilliseconds(1);
        public TimeSpan ConfiguredDeactivationTimeout { get; init; } = DeactivationTimeout;
        public FakeTimeProvider CollectionTime { get; } = new();
        private LifecycleCallbackRegistry Registry => HostedCluster.Silos[0].ServiceProvider.GetRequiredService<LifecycleCallbackRegistry>();

        public ILifecycleCallbackGrain GetGrain(LifecycleCallback callback)
        {
            var grain = GrainFactory.GetGrain<ILifecycleCallbackGrain>($"{callback}/{Guid.NewGuid():N}");
            Registry.Track(grain.GetGrainId());
            return grain;
        }

        public async Task<LifecycleCallbackProbe> ReadProbe(ILifecycleCallbackGrain grain) =>
            await Registry.Get(grain.GetGrainId()).Probes.Reader.ReadAsync(Cancellation).AsTask().WaitAsync(Timeout, Cancellation);

        public string[] GetTimeline(ILifecycleCallbackGrain grain) => Registry.Get(grain.GetGrainId()).Snapshot();

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.AddSingleton<LifecycleCallbackRegistry>();
                silo.Services.AddScoped<LifecycleCallbackProbe>();
                silo.Services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.ActivationManagement, CollectionTime);
                silo.Services.Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = ConfiguredDeactivationTimeout);
            });
        }

        public override async ValueTask DisposeAsync()
        {
            Registry.ReleaseAll();
            await base.DisposeAsync();
        }
    }
}

public enum LifecycleCallback
{
    None,
    OnActivateAsync,
    LifecycleStart,
    OnDeactivateAsync,
    LifecycleStop,
}

public readonly record struct CallbackObservation(bool ScopeDisposed, bool IsMessageTarget, bool Deactivated, int ActivationCount);

public sealed class LifecycleCallbackRegistry : IDisposable
{
    private readonly ConcurrentDictionary<GrainId, Timeline> _timelines = new();
    private readonly IDisposable _subscription;

    public LifecycleCallbackRegistry() => _subscription = GrainLifecycleEvents.AllEvents.Subscribe(new EventObserver(this));

    public void Track(GrainId grainId) => Assert.True(_timelines.TryAdd(grainId, new()));

    public Timeline Get(GrainId grainId) => _timelines[grainId];

    public void ReleaseAll()
    {
        foreach (var timeline in _timelines.Values)
        {
            timeline.ReleaseAll();
        }
    }

    public void Dispose() => _subscription.Dispose();

    private void OnEvent(GrainLifecycleEvents.LifecycleEvent lifecycleEvent)
    {
        if (_timelines.TryGetValue(lifecycleEvent.GrainContext.GrainId, out var timeline))
        {
            timeline.Add(lifecycleEvent.GetType().Name);
        }
    }

    public sealed class Timeline
    {
        private readonly List<string> _entries = [];
        private readonly ConcurrentQueue<LifecycleCallbackProbe> _created = new();

        public Channel<LifecycleCallbackProbe> Probes { get; } = Channel.CreateUnbounded<LifecycleCallbackProbe>();

        public int ActivationCount => _created.Count;

        public bool AddProbe(LifecycleCallbackProbe probe)
        {
            _created.Enqueue(probe);
            Assert.True(Probes.Writer.TryWrite(probe));
            return _created.Count == 1;
        }

        public void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        public string[] Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        public void ReleaseAll()
        {
            foreach (var probe in _created)
            {
                probe.Release.TrySetResult();
            }
        }
    }

    private sealed class EventObserver(LifecycleCallbackRegistry registry) : IObserver<GrainLifecycleEvents.LifecycleEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(GrainLifecycleEvents.LifecycleEvent value) => registry.OnEvent(value);
    }
}

public sealed class LifecycleCallbackProbe : IDisposable
{
    private readonly ActivationDirectory _activations;
    private readonly LifecycleCallbackRegistry.Timeline _timeline;
    private readonly LifecycleCallback _blockingCallback;
    private readonly bool _isFirstActivation;

    public LifecycleCallbackProbe(IGrainContext context, IServiceProvider services, LifecycleCallbackRegistry registry)
    {
        Context = context;
        _activations = services.GetRequiredService<ActivationDirectory>();
        var key = context.GrainId.Key.ToString()!;
        _blockingCallback = Enum.Parse<LifecycleCallback>(key.AsSpan(0, key.IndexOf('/')));
        _timeline = registry.Get(context.GrainId);
        _isFirstActivation = _timeline.AddProbe(this);
    }

    public IGrainContext Context { get; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<CallbackObservation> Observation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ScopeDisposed { get; private set; }
    public bool CancelCallback { get; set; }
    public int DeactivationCalls { get; private set; }
    public bool? DeactivationTokenCanceledOnEntry { get; private set; }

    public Task Run(LifecycleCallback callback, CancellationToken cancellationToken)
    {
        if (callback == LifecycleCallback.OnDeactivateAsync)
        {
            DeactivationCalls++;
            DeactivationTokenCanceledOnEntry = cancellationToken.IsCancellationRequested;
            _timeline.Add("OnDeactivateAsync");
        }

        return _isFirstActivation && callback == _blockingCallback ? RunPastCancellation(cancellationToken) : Task.CompletedTask;
    }

    public void Dispose() => ScopeDisposed = true;

    private async Task RunPastCancellation(CancellationToken cancellationToken)
    {
        Entered.SetResult();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).SetResult(), canceled);
        await canceled.Task;
        Canceled.SetResult();

        // Model user code which keeps running after its token is canceled.
        await Release.Task;
        _timeline.Add("CallbackCompleted");
        Observation.SetResult(new(
            ScopeDisposed,
            ReferenceEquals(_activations.FindTarget(Context.GrainId), Context),
            Context.Deactivated.IsCompleted,
            _timeline.ActivationCount));
        if (CancelCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

public interface ILifecycleCallbackGrain : IGrainWithStringKey
{
    Task Ping();
}

public sealed class LifecycleCallbackGrain(LifecycleCallbackProbe probe) : Grain, ILifecycleCallbackGrain, ILifecycleParticipant<IGrainLifecycle>
{
    public Task Ping() => Task.CompletedTask;

    public void Participate(IGrainLifecycle lifecycle) =>
        lifecycle.Subscribe<LifecycleCallbackGrain>(
            GrainLifecycleStage.SetupState,
            cancellationToken => probe.Run(LifecycleCallback.LifecycleStart, cancellationToken),
            cancellationToken => probe.Run(LifecycleCallback.LifecycleStop, cancellationToken));

    public override Task OnActivateAsync(CancellationToken cancellationToken) => probe.Run(LifecycleCallback.OnActivateAsync, cancellationToken);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) =>
        probe.Run(LifecycleCallback.OnDeactivateAsync, cancellationToken);
}
