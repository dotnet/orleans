using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Messaging;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>A grain's journaled effect counters and its messaging dead letters.</summary>
[GenerateSerializer, Immutable]
public sealed class FaultGrainState
{
    [Id(0)]
    public required Dictionary<string, int> Effects { get; init; }

    [Id(1)]
    public required string[] InboxDeadLetters { get; init; }

    [Id(2)]
    public required string[] OutboxDeadLetters { get; init; }
}

public interface IFaultInjectedGrain : IGrainWithGuidKey
{
    Task<FaultGrainState> GetStateAsync();

    Task DeactivateAsync();
}

public interface IFaultWorkflowGrain : IFaultInjectedGrain
{
    /// <summary>
    /// Runs a local step, a <see cref="DurableTask.RunInline(string, Action)"/> effect, a call to the partner which signals the
    /// workflow back, a signal wait, a delay, and a detached start on the partner which it awaits.
    /// </summary>
    DurableTask<string> PipelineAsync(Guid partnerKey, string rootId);

    /// <summary>
    /// Races a call to the partner's hold against a local gate which waits for the hold's signal and a delay, so the hold
    /// loses after its body started and compensates through a shielded child; then times out a signal wait.
    /// </summary>
    DurableTask<string> RaceAsync(Guid partnerKey, string rootId);

    /// <summary>Calls the partner, which throws, and records the exception's type and message.</summary>
    DurableTask<string> CatchPartnerFailureAsync(Guid partnerKey);

    /// <summary>Calls the partner, which throws, and lets the exception escape.</summary>
    DurableTask<int> PropagatePartnerFailureAsync(Guid partnerKey);

    /// <summary>Records an effect, then throws <see cref="FaultApplicationException"/>.</summary>
    DurableTask<int> FailAsync(string message);

    /// <summary>Throws <see cref="FaultApplicationException"/> from a local step, and records what the body observed.</summary>
    DurableTask<string> CatchLocalStepFailureAsync(string message);

    /// <summary>Throws <see cref="FaultApplicationException"/> from a <see cref="DurableTask.RunInline(string, Action)"/> effect.</summary>
    DurableTask<string> CatchOnceFailureAsync(string message);

    /// <summary>Starts a detached task on the partner under a root ID, and records what the start observed.</summary>
    DurableTask<string> StartDetachedEchoAsync(Guid partnerKey, string detachedRootId, int value);

    /// <summary>Waits for the <see cref="FaultWorkflowGrain.Ready"/> signal.</summary>
    DurableTask<int> WaitForReadyAsync();

    /// <summary>Calls the partner's gated step.</summary>
    DurableTask<int> CallGatedAsync(Guid partnerKey, int value);

    /// <summary>Starts a detached echo on a <see cref="IFaultFencedGrain"/>, and records what the start observed.</summary>
    DurableTask<string> StartDetachedFencedEchoAsync(Guid fencedKey, string detachedRootId, int value);
}

public interface IFaultPartnerGrain : IFaultInjectedGrain
{
    DurableTask<int> ProcessAsync(int value, Guid workflowKey, string rootId);

    DurableTask<int> EchoAsync(int value);

    DurableTask<string> HoldAsync(Guid workflowKey, string rootId);

    /// <summary>Records an effect, then throws <see cref="FaultApplicationException"/>.</summary>
    DurableTask<int> FailAsync(string message);

    /// <summary>Signals <see cref="FaultWorkflowGrain.Ready"/> to a task of a <see cref="IFaultFencedGrain"/>.</summary>
    DurableTask SignalFencedAsync(Guid fencedKey, string rootId, int value);

    /// <summary>Runs a local step which waits for the partner's <see cref="FaultTestGates"/> gate.</summary>
    DurableTask<int> GatedStepAsync(int value);
}

/// <summary>A reentrant grain whose journal a test fences while a delivery to it is in progress.</summary>
public interface IFaultFencedGrain : IFaultInjectedGrain
{
    DurableTask<int> EchoAsync(int value);

    /// <summary>Calls the partner's echo, whose completion this grain's inbox accepts.</summary>
    DurableTask<int> CallEchoAsync(Guid partnerKey, int value);

    /// <summary>Waits for the <see cref="FaultWorkflowGrain.Ready"/> signal.</summary>
    DurableTask<int> WaitForReadyAsync();

    /// <summary>Stages a journaled effect and writes it, and returns the write's failure, if any.</summary>
    Task<string> FenceAsync();
}

/// <summary>Gates which test grains wait for, and which the test opens, keyed by grain.</summary>
public sealed class FaultTestGates
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();

    public Task WaitAsync(Guid key, CancellationToken cancellationToken) => Get(key).Task.WaitAsync(cancellationToken);

    public void Open(Guid key) => Get(key).TrySetResult();

    private TaskCompletionSource Get(Guid key) =>
        _gates.GetOrAdd(key, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>An application failure, which every awaiter, poller, and subscriber observes unchanged.</summary>
[GenerateSerializer]
public sealed class FaultApplicationException : Exception
{
    public FaultApplicationException(string message) : base(message)
    {
    }
}

/// <summary>
/// A durable grain which records journaled effect counters and registers the injector's inbox participant ahead of the
/// durable task runtime's sink and handler.
/// </summary>
/// <remarks>
/// The grain is constructed before activation setup registers the runtime's sink and handler, so the participant is first.
/// </remarks>
public abstract class FaultInjectedGrain : DurableGrain, IFaultInjectedGrain
{
    private readonly IDurableDictionary<string, int> _effects;
    private readonly IMessagingDiagnostics _diagnostics;

    protected FaultInjectedGrain(
        IInbox inbox,
        IMessagingDiagnostics diagnostics,
        IDurableDictionary<string, int> effects,
        DurableTaskFaultInjector injector,
        IGrainContext grainContext)
    {
        var participant = new FaultInjectingInboxParticipant(injector, grainContext);
        inbox.Register((IInboxMessageSink)participant).Register((IInboxHandler)participant);
        _effects = effects;
        _diagnostics = diagnostics;
    }

    public Task<FaultGrainState> GetStateAsync() => Task.FromResult(new FaultGrainState
    {
        Effects = _effects.ToDictionary(),
        InboxDeadLetters = [.. _diagnostics.InboxDeadLetters.Select(static letter => $"{letter.Message.Envelope.MessageId}: {letter.Reason}")],
        OutboxDeadLetters = [.. _diagnostics.OutboxDeadLetters.Select(static letter => $"{letter.Message.Envelope.MessageId}: {letter.Reason}")],
    });

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>Increments a journaled effect counter; call it from a <see cref="DurableTask.RunInline(string, Func{int})"/> effect.</summary>
    protected int Increment(string effect)
    {
        var count = _effects.TryGetValue(effect, out var current) ? current + 1 : 1;
        _effects[effect] = count;
        return count;
    }
}

[GrainType("durable-task-fault-workflow-test")]
public sealed class FaultWorkflowGrain(
    IInbox inbox,
    IMessagingDiagnostics diagnostics,
    [FromKeyedServices("effects")] IDurableDictionary<string, int> effects,
    DurableTaskFaultInjector injector,
    IGrainContext grainContext)
    : FaultInjectedGrain(inbox, diagnostics, effects, injector, grainContext), IFaultWorkflowGrain
{
    public static readonly DurableSignal<int> Ready = new("ready");
    public static readonly DurableSignal<string> Held = new("held");
    public static readonly DurableSignal<string> Never = new("never");

    public async DurableTask<string> PipelineAsync(Guid partnerKey, string rootId)
    {
        var partner = Partner(partnerKey);
        var step = await DurableTask.Run(static _ => 20).WithId("step");
        var effect = DurableTask.RunInline("workflow", () => Increment("workflow"));
        var processed = await partner.ProcessAsync(step, this.GetPrimaryKey(), rootId).WithId("process");
        var ready = await Ready.WaitAsync();
        await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("pause");
        var echoed = await partner.EchoAsync(processed).Detached().WithId($"{rootId}-echo");
        return $"step={step};effect={effect};processed={processed};ready={ready};echoed={echoed}";
    }

    public async DurableTask<string> RaceAsync(Guid partnerKey, string rootId)
    {
        var (winner, outcome) = await DurableTask.Race([Partner(partnerKey).HoldAsync(this.GetPrimaryKey(), rootId), GateAsync()])
            .WithId("race");
        string deadline;
        try
        {
            deadline = await Never.WaitAsync().WaitAsync(TimeSpan.FromMinutes(5)).WithId("deadline");
        }
        catch (TimeoutException)
        {
            deadline = "timed-out";
        }

        var effect = DurableTask.RunInline("race", () => Increment("race"));
        return $"winner={winner};outcome={outcome};deadline={deadline};effect={effect}";
    }

    public async DurableTask<string> CatchPartnerFailureAsync(Guid partnerKey)
    {
        try
        {
            await Partner(partnerKey).FailAsync("partner failed").WithId("fail");
            return "completed";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public async DurableTask<int> PropagatePartnerFailureAsync(Guid partnerKey) =>
        await Partner(partnerKey).FailAsync("partner failed").WithId("fail");

    public async DurableTask<int> FailAsync(string message)
    {
        DurableTask.RunInline("fail", () => Increment("fail"));
        await DurableTask.Run(static _ => { }).WithId("before-failure");
        throw new FaultApplicationException(message);
    }

    public async DurableTask<string> CatchLocalStepFailureAsync(string message)
    {
        try
        {
            await DurableTask.Run(int (_) => throw new FaultApplicationException(message)).WithId("step");
            return "completed";
        }
        catch (Exception exception)
        {
            DurableTask.RunInline("caught", () => Increment("caught"));
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public async DurableTask<string> CatchOnceFailureAsync(string message)
    {
        string observed;
        try
        {
            DurableTask.RunInline("throwing", int () =>
            {
                Increment("throwing");
                throw new FaultApplicationException(message);
            });
            observed = "completed";
        }
        catch (Exception exception)
        {
            observed = $"{exception.GetType().Name}: {exception.Message}";
        }

        // A later durable write commits the effect's record, so a replay observes the recorded failure.
        await DurableTask.Run(static _ => { }).WithId("after");
        return observed;
    }

    public async DurableTask<string> StartDetachedEchoAsync(Guid partnerKey, string detachedRootId, int value)
    {
        try
        {
            return $"echoed {await Partner(partnerKey).EchoAsync(value).Detached().WithId(detachedRootId)}";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public async DurableTask<int> WaitForReadyAsync() => await Ready.WaitAsync();

    public async DurableTask<int> CallGatedAsync(Guid partnerKey, int value) =>
        await Partner(partnerKey).GatedStepAsync(value).WithId("gated");

    public async DurableTask<string> StartDetachedFencedEchoAsync(Guid fencedKey, string detachedRootId, int value)
    {
        try
        {
            return $"echoed {await GrainFactory.GetGrain<IFaultFencedGrain>(fencedKey).EchoAsync(value).Detached().WithId(detachedRootId)}";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static async DurableTask<string> GateAsync()
    {
        var held = await Held.WaitAsync();
        await DurableTask.Delay(TimeSpan.FromMinutes(10));
        return $"gate after {held}";
    }

    private IFaultPartnerGrain Partner(Guid key) => GrainFactory.GetGrain<IFaultPartnerGrain>(key);
}

[GrainType("durable-task-fault-partner-test")]
public sealed class FaultPartnerGrain(
    IInbox inbox,
    IMessagingDiagnostics diagnostics,
    [FromKeyedServices("effects")] IDurableDictionary<string, int> effects,
    DurableTaskFaultInjector injector,
    IGrainContext grainContext,
    FaultTestGates gates,
    DurableTaskMessagingProbe probe)
    : FaultInjectedGrain(inbox, diagnostics, effects, injector, grainContext), IFaultPartnerGrain
{
    public async DurableTask<int> GatedStepAsync(int value) =>
        await DurableTask.Run(async cancellationToken =>
        {
            probe.RecordStarted(this.GetPrimaryKey());
            await gates.WaitAsync(this.GetPrimaryKey(), cancellationToken);
            return value + DurableTask.RunInline("gated", () => Increment("gated"));
        }).WithId("gated");


    public async DurableTask<int> ProcessAsync(int value, Guid workflowKey, string rootId)
    {
        var effect = DurableTask.RunInline("process", () => Increment("process"));
        await Workflow(workflowKey).SignalDurableTaskAsync(TaskId.CreateRoot(rootId), FaultWorkflowGrain.Ready, value * 3);
        return (value * 2) + effect - 1;
    }

    public DurableTask<int> EchoAsync(int value) =>
        DurableTask.Run(_ => value + DurableTask.RunInline("echo", () => Increment("echo")));

    public async DurableTask<string> HoldAsync(Guid workflowKey, string rootId)
    {
        try
        {
            // The workflow can cancel the hold as soon as the signal arrives, which can precede the send's own completion.
            await Workflow(workflowKey).SignalDurableTaskAsync(TaskId.CreateRoot(rootId), FaultWorkflowGrain.Held, "held");
            await DurableTask.Run(static async cancellationToken => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
                .WithId("hold");
            return "released";
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            var compensation = await DurableTask.Run(_ => DurableTask.RunInline("compensate", () => Increment("compensate")))
                .WithId("compensate")
                .Shielded();
            return $"compensated {compensation}";
        }
    }

    public async DurableTask<int> FailAsync(string message)
    {
        DurableTask.RunInline("fail", () => Increment("fail"));
        await DurableTask.Run(static _ => { }).WithId("before-failure");
        throw new FaultApplicationException(message);
    }

    public async DurableTask SignalFencedAsync(Guid fencedKey, string rootId, int value) =>
        await GrainFactory.GetGrain<IFaultFencedGrain>(fencedKey).SignalDurableTaskAsync(TaskId.CreateRoot(rootId), FaultWorkflowGrain.Ready, value);

    private IFaultWorkflowGrain Workflow(Guid key) => GrainFactory.GetGrain<IFaultWorkflowGrain>(key);
}

[Reentrant]
[GrainType("durable-task-fault-fenced-test")]
public sealed class FaultFencedGrain(
    IInbox inbox,
    IMessagingDiagnostics diagnostics,
    [FromKeyedServices("effects")] IDurableDictionary<string, int> effects,
    DurableTaskFaultInjector injector,
    IGrainContext grainContext)
    : FaultInjectedGrain(inbox, diagnostics, effects, injector, grainContext), IFaultFencedGrain
{
    public DurableTask<int> EchoAsync(int value) =>
        DurableTask.Run(_ => value + DurableTask.RunInline("echo", () => Increment("echo")));

    public async DurableTask<int> CallEchoAsync(Guid partnerKey, int value) =>
        await GrainFactory.GetGrain<IFaultPartnerGrain>(partnerKey).EchoAsync(value).WithId("echo");

    public async DurableTask<int> WaitForReadyAsync() => await FaultWorkflowGrain.Ready.WaitAsync();

    public async Task<string> FenceAsync()
    {
        Increment("fence");
        string outcome;
        try
        {
            await WriteStateAsync();
            outcome = "written";
        }
        catch (Exception exception)
        {
            outcome = exception.Message;
        }

        return outcome;
    }
}
