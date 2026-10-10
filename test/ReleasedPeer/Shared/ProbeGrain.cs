using System.Collections.Concurrent;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;
using ReleasedPeer.Contracts;

namespace ReleasedPeer;

public sealed class ProbeControl
{
    public const string ContextKey = "released-peer-operation";
    public bool HoldDeactivation { get; set; }
    public bool HoldActivation { get; set; }
    public IGrainContext? GrainContext { get; set; }
    public TaskCompletionSource DeactivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDeactivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RunningEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRunning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<InvocationResult> Entries { get; } = new();
}

[PreferLocalPlacement]
public sealed class RetirementProbeGrain(ProbeControl control, ILocalSiloDetails local) : Grain, IRetirementProbeGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        control.GrainContext = GrainContext;
        control.ActivationEntered.TrySetResult();
        if (control.HoldActivation)
        {
            await control.ReleaseActivation.Task.WaitAsync(cancellationToken);
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (control.HoldDeactivation)
        {
            control.DeactivationEntered.TrySetResult();
            await control.ReleaseDeactivation.Task.WaitAsync(cancellationToken);
        }
    }

    public Task<string> Locate() => Task.FromResult(local.SiloAddress.ToParsableString());

    public async Task Block()
    {
        control.RunningEntered.TrySetResult();
        await control.ReleaseRunning.Task;
    }

    public Task<InvocationResult> Execute(Guid operationId, int argument)
    {
        var result = new InvocationResult(
            operationId, checked(argument * 3 + 7), local.SiloAddress.ToParsableString(),
            RequestContext.Get(ProbeControl.ContextKey) as string ?? "<missing>",
            control.Entries.Count + 1);
        control.Entries.Enqueue(result);
        return Task.FromResult(result);
    }
}
