using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost;
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
    [InlineData(LifecycleCallback.OnActivateAsync)]
    [InlineData(LifecycleCallback.LifecycleStart)]
    public async Task CanceledActivation_AwaitsCallbackBeforeReleasingActivation(LifecycleCallback callback)
    {
        var grain = fixture.GetGrain(callback);
        var call = grain.Ping();
        var probe = await fixture.ReadProbe(grain);
        await probe.Entered.Task.WaitAsync(Timeout, Cancellation);

        // Deactivating during activation cancels the activation token, which is the token canceled by ActivationTimeout.
        probe.Context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Canceled during activation."), Cancellation);
        await AssertCallbackAwaitedPastCancellation(probe);
        Assert.False(call.IsCompleted);

        await call.WaitAsync(Timeout, Cancellation);
        Assert.True(probe.ScopeDisposed);
        Assert.Equal(
            ["Created", "Deactivating", "CallbackCompleted", "Deactivated", "Created", "Activated"],
            fixture.GetTimeline(grain));
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
        await AssertCallbackAwaitedPastCancellation(probe);
        Assert.False(call.IsCompleted);

        await call.WaitAsync(Timeout, Cancellation);
        Assert.True(probe.ScopeDisposed);
        Assert.Equal(
            ["Created", "Activated", "Deactivating", "CallbackCompleted", "Deactivated", "Created", "Activated"],
            fixture.GetTimeline(grain));
    }

    private static async Task AssertCallbackAwaitedPastCancellation(LifecycleCallbackProbe probe)
    {
        await probe.Canceled.Task.WaitAsync(Timeout, Cancellation);

        // Cancellation has already queued any runtime continuation on the activation scheduler, so release runs after it.
        probe.Context.Scheduler.QueueAction(() => probe.Release.SetResult());
        var observation = await probe.Observation.Task.WaitAsync(Timeout, Cancellation);
        Assert.Equal(new CallbackObservation(ScopeDisposed: false, IsMessageTarget: true, Deactivated: false, ActivationCount: 1), observation);

        await probe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
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
                silo.Services.Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = TimeSpan.FromMilliseconds(1));
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

    public Task Run(LifecycleCallback callback, CancellationToken cancellationToken) =>
        _isFirstActivation && callback == _blockingCallback ? RunPastCancellation(cancellationToken) : Task.CompletedTask;

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
