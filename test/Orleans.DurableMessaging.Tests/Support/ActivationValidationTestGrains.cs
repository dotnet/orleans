using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class ActivationValidationProbe
{
    private readonly ConcurrentDictionary<GrainId, ConcurrentQueue<Observation>> _observations = new();
    public Observation[] Get(GrainId grainId) => _observations.TryGetValue(grainId, out var values) ? values.ToArray() : [];
    public Observation Track(IGrainContext context)
    {
        var observation = new Observation(context);
        _observations.GetOrAdd(context.GrainId, static _ => new()).Enqueue(observation);
        return observation;
    }

    public sealed class Observation(IGrainContext context)
    {
        public IGrainContext Context { get; } = context;
        public bool InstanceAvailableDuringConstruction { get; } = context.GrainInstance is not null;
        public int ReplayStarted;
        public int ReplayCompleted;
        public int Activated;
        public int Calls;
    }
}

public interface IActivationValidationTestGrain : IGrainWithGuidKey
{
    Task<int> IncrementAsync();
    Task DeactivateAsync();
}

public interface IAlwaysInterleaveValidationTestGrain : IActivationValidationTestGrain
{
    [AlwaysInterleave]
    Task InterleaveAsync();
}

public abstract class ActivationValidationTestGrain : DurableGrain, IActivationValidationTestGrain, IJournaledStateObserver
{
    private readonly IDurableValue<int> _value;
    private readonly ActivationValidationProbe.Observation _observation;

    protected ActivationValidationTestGrain(IGrainContext context, ActivationValidationProbe probe,
        IDurableValue<int> value)
    {
        _value = value;
        _observation = probe.Track(context);
        StateManager.RegisterObserver(this);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _observation.Activated);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<int> IncrementAsync()
    {
        Interlocked.Increment(ref _observation.Calls);
        _value.Value++;
        await WriteStateAsync();
        return _value.Value;
    }

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public void OnRecoveryStarted() => Interlocked.Increment(ref _observation.ReplayStarted);
    public void OnRecoveryCompleted() => Interlocked.Increment(ref _observation.ReplayCompleted);
    public void OnWriteStarted() { }
    public void OnWriteCompleted() { }
}

public sealed class SupportedActivationValidationTestGrain(
    IGrainContext context, ActivationValidationProbe probe,
    [FromKeyedServices("activation-validation")] IDurableValue<int> value)
    : ActivationValidationTestGrain(context, probe, value);

[Reentrant]
public sealed class ReentrantActivationValidationTestGrain(
    IGrainContext context, ActivationValidationProbe probe,
    [FromKeyedServices("activation-validation")] IDurableValue<int> value)
    : ActivationValidationTestGrain(context, probe, value);

[StatelessWorker]
public sealed class StatelessActivationValidationTestGrain(
    IGrainContext context, ActivationValidationProbe probe,
    [FromKeyedServices("activation-validation")] IDurableValue<int> value)
    : ActivationValidationTestGrain(context, probe, value);

[MayInterleave(nameof(Interleave))]
public sealed class MayInterleaveActivationValidationTestGrain(
    IGrainContext context, ActivationValidationProbe probe,
    [FromKeyedServices("activation-validation")] IDurableValue<int> value)
    : ActivationValidationTestGrain(context, probe, value)
{
    public static bool Interleave(IInvokable request) => true;
}

public sealed class AlwaysInterleaveActivationValidationTestGrain(
    IGrainContext context, ActivationValidationProbe probe,
    [FromKeyedServices("activation-validation")] IDurableValue<int> value)
    : ActivationValidationTestGrain(context, probe, value), IAlwaysInterleaveValidationTestGrain
{
    public Task InterleaveAsync() => Task.CompletedTask;
}
