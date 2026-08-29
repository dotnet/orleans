using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

public interface IDurableTaskRecoveryTestGrain : IGrainWithGuidKey
{
    DurableTask<int> ComputeAsync(int value);
    Task<Guid> GetActivationIdAsync();
    Task RequestDeactivationAsync();
}

public readonly record struct DurableTaskInvocationSnapshot(int Count, Guid ActivationId, int Argument);

public sealed class DurableTaskExecutionProbe
{
    private readonly ConcurrentDictionary<GrainId, DurableTaskInvocationSnapshot> _invocations = [];

    public void Record(GrainId grainId, Guid activationId, int argument) =>
        _invocations.AddOrUpdate(
            grainId,
            _ => new DurableTaskInvocationSnapshot(1, activationId, argument),
            (_, current) => new DurableTaskInvocationSnapshot(current.Count + 1, activationId, argument));

    public DurableTaskInvocationSnapshot GetSnapshot(GrainId grainId) =>
        _invocations.TryGetValue(grainId, out var snapshot) ? snapshot : default;
}

[GrainType("durable-task-recovery-test")]
public sealed class DurableTaskRecoveryTestGrain(DurableTaskExecutionProbe probe)
    : DurableGrain, IDurableTaskRecoveryTestGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public DurableTask<int> ComputeAsync(int value)
    {
        probe.Record(this.GetGrainId(), _activationId, value);
        return DurableTask.FromResult(checked((value * 3) + 7));
    }

    public Task<Guid> GetActivationIdAsync() => Task.FromResult(_activationId);

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
