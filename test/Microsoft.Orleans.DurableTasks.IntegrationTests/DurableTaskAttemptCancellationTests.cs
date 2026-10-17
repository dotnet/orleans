using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Runs attempt cancellation coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskAttemptCancellationTests() : DurableTaskAttemptCancellationTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs attempt cancellation coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskAttemptCancellationTests() : DurableTaskAttemptCancellationTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>
/// Tests that a canceled attempt of a durable task unwinds without recording anything, while task cancellation records
/// its compensation.
/// </summary>
public abstract class DurableTaskAttemptCancellationTests : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture;

    protected DurableTaskAttemptCancellationTests(string journalFormatKey)
    {
        var probe = new AttemptCancellationProbe();
        Probe = probe;
        _fixture = new(journalFormatKey, silo => silo.Services.AddSingleton(probe));
    }

    private AttemptCancellationProbe Probe { get; }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task DeactivationCancelsTheAttemptWhichUnwindsWithoutRecordingAnything()
    {
        const string rootId = "deactivated";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var scheduled = await grain.WaitForCancellationAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Waiting, cancellationToken);
        Assert.True(_fixture.Cluster.TryGetGrainContext(grainId, out var activation));

        // The grain's OnDeactivateAsync commits a journal write while the canceled attempt is still unwinding.
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        AssertCanceledAttemptUnwound(grainId, DurableTaskAttemptCancellationReason.ActivationStopping);
        Assert.Equal(1, Probe.Get(grainId, AttemptCancellationProbe.ConcurrentWrites));

        // The next activation recovers the committed journal, in which the canceled attempt changed nothing, and replays the
        // task, which completes once it receives its signal without running its effect again.
        var snapshot = await grain.GetSnapshotAsync();
        Assert.Equal(new AttemptCancellationSnapshot(Status: "running", Unrelated: 1), snapshot);
        await grain.SignalDurableTaskAsync(rootId, AttemptCancellationTestGrain.Approved, "approved", cancellationToken);
        Assert.Equal("approved", (await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).GetResult<string>());
        Assert.Equal(1, Probe.Get(grainId, AttemptCancellationProbe.EffectRuns));
        Assert.Equal(1, Probe.Get(grainId, AttemptCancellationProbe.Catches));
    }

    [Fact]
    public async Task AbortCancelsTheAttemptWhileOtherExecutionsOfTheGrainKeepCommitting()
    {
        const string rootId = "aborted";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        Probe.HoldUnwinding(grainId);
        var scheduled = await grain.WaitForCancellationAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Waiting, cancellationToken);

        await scheduled.AbortAsync("operator", cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Unwinding, cancellationToken);

        // Another execution of the grain, which stays active, commits while the aborted attempt unwinds.
        var increment = await grain.IncrementAsync().WithId("increment").ScheduleAsync(cancellationToken);
        Assert.Equal(1, await increment.WaitAsync(cancellationToken));
        Probe.Signal(grainId, AttemptCancellationProbe.Release);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Unwound, cancellationToken);

        var aborted = await Assert.ThrowsAsync<DurableTaskAbortedException>(async () => await scheduled.WaitAsync(cancellationToken));
        Assert.Equal("operator", aborted.Message);
        AssertCanceledAttemptUnwound(grainId, DurableTaskAttemptCancellationReason.TaskAborted);
        Assert.Equal(new AttemptCancellationSnapshot(Status: "running", Unrelated: 1), await grain.GetSnapshotAsync());

        // The committed journal holds the same state.
        Assert.True(_fixture.Cluster.TryGetGrainContext(grainId, out var activation));
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(new AttemptCancellationSnapshot(Status: "running", Unrelated: 1), await grain.GetSnapshotAsync());
        Assert.Equal(1, Probe.Get(grainId, AttemptCancellationProbe.EffectRuns));
    }

    [Fact]
    public async Task TaskCancellationRecordsCompensationMadeWithTheAttemptCancellationToken()
    {
        const string rootId = "compensated";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        Probe.Signal(grainId, AttemptCancellationProbe.Release);
        var scheduled = await grain.CompensateOnCancellationAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Waiting, cancellationToken);

        await scheduled.CancelAsync("buyer", cancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scheduled.WaitAsync(cancellationToken));
        Assert.Equal(new AttemptCancellationSnapshot(Status: "compensated: buyer", Unrelated: 0), await grain.GetSnapshotAsync());
        Assert.Equal((1, 1, 1), (Probe.Get(grainId, AttemptCancellationProbe.Compensations), Probe.Get(grainId, AttemptCancellationProbe.Refunds), Probe.Get(grainId, AttemptCancellationProbe.ExternalCalls)));

        // The committed journal holds the compensation.
        Assert.True(_fixture.Cluster.TryGetGrainContext(grainId, out var activation));
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(new AttemptCancellationSnapshot(Status: "compensated: buyer", Unrelated: 0), await grain.GetSnapshotAsync());
    }

    [Fact]
    public async Task AttemptCancellationDuringCompensationLeavesItForTheReplayToRedo()
    {
        const string rootId = "interrupted-compensation";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var scheduled = await grain.CompensateOnCancellationAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Waiting, cancellationToken);
        await scheduled.CancelAsync("buyer", cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Compensating, cancellationToken);
        Assert.True(_fixture.Cluster.TryGetGrainContext(grainId, out var activation));

        // The compensation's external call observes the attempt's cancellation, and its later changes don't commit.
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var interrupted = Probe.GetOutcomes(grainId);
        Assert.IsType<TaskCanceledException>(interrupted["external-call"]);
        var fenced = Assert.IsType<DurableTaskAttemptCanceledException>(interrupted["after-external-call"]);
        Assert.Equal(DurableTaskAttemptCancellationReason.ActivationStopping, fenced.Reason);

        // The replay finds the effect recorded and redoes the rest of the compensation.
        Probe.Signal(grainId, AttemptCancellationProbe.Release);
        _ = await grain.GetSnapshotAsync();
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal(new AttemptCancellationSnapshot(Status: "compensated: buyer", Unrelated: 0), await grain.GetSnapshotAsync());
        Assert.Equal((1, 1, 2), (Probe.Get(grainId, AttemptCancellationProbe.Compensations), Probe.Get(grainId, AttemptCancellationProbe.Refunds), Probe.Get(grainId, AttemptCancellationProbe.ExternalCalls)));
    }

    private void AssertCanceledAttemptUnwound(GrainId grainId, DurableTaskAttemptCancellationReason reason)
    {
        var outcomes = Probe.GetOutcomes(grainId);
        var caught = Assert.IsType<DurableTaskAttemptCanceledException>(outcomes[AttemptCancellationProbe.Caught]);
        Assert.Equal(reason, caught.Reason);
        Assert.Null(outcomes[AttemptCancellationProbe.TaskCancellationRequested]);
        foreach (var operation in AttemptCancellationTestGrain.CanceledAttemptOperations)
        {
            var exception = Assert.IsType<DurableTaskAttemptCanceledException>(outcomes[operation]);
            Assert.Equal(reason, exception.Reason);
        }

        // In-process resources are released on the way out.
        Assert.Null(outcomes[AttemptCancellationProbe.SemaphoreReleased]);
        Assert.Null(outcomes[AttemptCancellationProbe.ResourceDisposed]);
    }
}

public interface IAttemptCancellationTestGrain : IGrainWithGuidKey
{
    /// <summary>Waits for a signal, and records what its code can do once its attempt is canceled.</summary>
    DurableTask<string> WaitForCancellationAsync();

    /// <summary>Waits for an hour-long delay, and compensates when the task is canceled.</summary>
    DurableTask<string> CompensateOnCancellationAsync();

    /// <summary>Increments the grain's unrelated counter.</summary>
    DurableTask<int> IncrementAsync();

    DurableTask<int> ComputeAsync(int value);

    Task<AttemptCancellationSnapshot> GetSnapshotAsync();

    Task RequestDeactivationAsync();
}

/// <summary>The grain's journaled state which a canceled attempt tries to change.</summary>
[GenerateSerializer]
public sealed record AttemptCancellationSnapshot(
    [property: Id(0)] string? Status,
    [property: Id(1)] int Unrelated,
    [property: Id(2)] int Changes = 0);

/// <summary>Records what the attempts of <see cref="AttemptCancellationTestGrain"/> observe, and synchronizes tests with them.</summary>
public sealed class AttemptCancellationProbe
{
    public const string Waiting = "waiting";
    public const string Unwinding = "unwinding";
    public const string Unwound = "unwound";
    public const string Release = "release";
    public const string Compensating = "compensating";
    public const string Caught = "caught";
    public const string TaskCancellationRequested = "task-cancellation-requested";
    public const string SemaphoreReleased = "semaphore-released";
    public const string ResourceDisposed = "resource-disposed";
    public const string EffectRuns = "effect-runs";
    public const string Catches = "catches";
    public const string ConcurrentWrites = "concurrent-writes";
    public const string Compensations = "compensations";
    public const string Refunds = "refunds";
    public const string ExternalCalls = "external-calls";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<(GrainId, string), TaskCompletionSource> _signals = new();
    private readonly ConcurrentDictionary<(GrainId, string), int> _counters = new();
    private readonly ConcurrentDictionary<GrainId, ConcurrentDictionary<string, Exception?>> _outcomes = new();
    private readonly ConcurrentDictionary<GrainId, bool> _heldUnwinding = new();

    public void Signal(GrainId grainId, string name) => GetSignal(grainId, name).TrySetResult();

    public async Task WaitAsync(GrainId grainId, string name, CancellationToken cancellationToken)
    {
        try
        {
            await GetSignal(grainId, name).Task.WaitAsync(Timeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Grain '{grainId}' didn't signal '{name}' within {Timeout}.", exception);
        }
    }

    /// <summary>Makes the grain's canceled attempt wait for <see cref="Release"/> while it unwinds.</summary>
    public void HoldUnwinding(GrainId grainId) => _heldUnwinding[grainId] = true;

    public bool IsUnwindingHeld(GrainId grainId) => _heldUnwinding.ContainsKey(grainId);

    public int Increment(GrainId grainId, string name) => _counters.AddOrUpdate((grainId, name), 1, static (_, count) => count + 1);

    public int Get(GrainId grainId, string name) => _counters.GetValueOrDefault((grainId, name));

    public void Record(GrainId grainId, string name, Exception? exception) =>
        _outcomes.GetOrAdd(grainId, static _ => new())[name] = exception;

    public IReadOnlyDictionary<string, Exception?> GetOutcomes(GrainId grainId) => _outcomes.GetOrAdd(grainId, static _ => new());

    private TaskCompletionSource GetSignal(GrainId grainId, string name) =>
        _signals.GetOrAdd((grainId, name), static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

[GrainType("durable-task-attempt-cancellation-test")]
public sealed class AttemptCancellationTestGrain(
    AttemptCancellationProbe probe,
    [FromKeyedServices("status")] IDurableValue<string> status,
    [FromKeyedServices("unrelated")] IDurableValue<int> unrelated,
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("list")] IDurableList<string> list,
    [FromKeyedServices("queue")] IDurableQueue<string> queue,
    [FromKeyedServices("set")] IDurableSet<string> set,
    [FromKeyedServices("log")] IDurableLog<string> log,
    [FromKeyedServices("completion")] IDurableTaskCompletionSource<int> completion,
    [FromKeyedServices("state")] IPersistentState<string> state)
    : DurableGrain, IAttemptCancellationTestGrain
{
    public static readonly DurableSignal<string> Approved = new("approved");

    /// <summary>The operations which a canceled attempt's code tries, each of which throws the attempt's cancellation.</summary>
    public static readonly string[] CanceledAttemptOperations =
    [
        "once", "child", "shielded-child", "detached-call", "call", "post", "schedule", "delay", "signal-wait",
        "signal-send", "subscription", "when-all", "when-any", "race", "timeout", "try-wait",
        "value", "dictionary", "list", "queue", "set", "log", "completion", "persistent-state", "persistent-state-write",
        "grain-write",
    ];

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetGrainId();
        if (probe.GetOutcomes(grainId).ContainsKey(AttemptCancellationProbe.Waiting) && !probe.IsUnwindingHeld(grainId))
        {
            // The runtime drains the canceled attempt while this method runs: this write commits while the attempt waits.
            await probe.WaitAsync(grainId, AttemptCancellationProbe.Unwinding, cancellationToken);
            unrelated.Value++;
            await WriteStateAsync(cancellationToken);
            probe.Increment(grainId, AttemptCancellationProbe.ConcurrentWrites);
            probe.Signal(grainId, AttemptCancellationProbe.Release);
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async DurableTask<string> WaitForCancellationAsync()
    {
        var context = DurableTask.Current;
        var grainId = this.GetGrainId();
        DurableTask.Once("started", () => probe.Increment(grainId, AttemptCancellationProbe.EffectRuns));
        status.Value = "running";
        await _semaphore.WaitAsync(context.CancellationToken);
        try
        {
            await using var resource = new ProbeResource(probe, grainId);
            await DurableTask.Run(static _ => { }).WithId("commit");
            probe.Record(grainId, AttemptCancellationProbe.Waiting, null);
            probe.Signal(grainId, AttemptCancellationProbe.Waiting);
            return await Approved.WaitAsync();
        }
        catch (OperationCanceledException exception)
        {
            probe.Increment(grainId, AttemptCancellationProbe.Catches);
            probe.Record(grainId, AttemptCancellationProbe.Caught, exception);
            probe.Record(
                grainId,
                AttemptCancellationProbe.TaskCancellationRequested,
                context.IsTaskCancellationRequested ? new InvalidOperationException("Task cancellation was requested.") : null);
            await TryCanceledAttemptOperationsAsync(grainId);
            probe.Signal(grainId, AttemptCancellationProbe.Unwinding);
            await probe.WaitAsync(grainId, AttemptCancellationProbe.Release, CancellationToken.None);
            throw;
        }
        finally
        {
            _semaphore.Release();
            probe.Record(
                grainId,
                AttemptCancellationProbe.SemaphoreReleased,
                _semaphore.CurrentCount == 1 ? null : new InvalidOperationException("The semaphore is held."));
            probe.Signal(grainId, AttemptCancellationProbe.Unwound);
        }
    }

    public async DurableTask<string> CompensateOnCancellationAsync()
    {
        var context = DurableTask.Current;
        var grainId = this.GetGrainId();
        try
        {
            var wait = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("wait").ScheduleAsync(context.CancellationToken);
            probe.Signal(grainId, AttemptCancellationProbe.Waiting);
            await wait;
            return "completed";
        }
        catch (OperationCanceledException) when (context.IsTaskCancellationRequested)
        {
            DurableTask.Once("compensate", () => probe.Increment(grainId, AttemptCancellationProbe.Compensations));
            status.Value = "compensating";
            // Compensation's children are shielded, because children which a canceled task starts end canceled.
            await DurableTask.Run(static _ => { }).WithId("checkpoint").Shielded();
            probe.Signal(grainId, AttemptCancellationProbe.Compensating);

            // A call which compensation makes uses the attempt's token, because the task's token is already canceled.
            try
            {
                await CallExternalServiceAsync(grainId, context.AttemptCancellationToken);
            }
            catch (OperationCanceledException exception) when (context.AttemptCancellationToken.IsCancellationRequested)
            {
                probe.Record(grainId, "external-call", exception);
                probe.Record(grainId, "after-external-call", Record(() => status.Value = "half-compensated"));
                throw;
            }

            await DurableTask.Run(_ => probe.Increment(grainId, AttemptCancellationProbe.Refunds)).WithId("refund").Shielded();
            status.Value = $"compensated: {context.TaskCancellationReason}";
            throw;
        }
    }

    public DurableTask<int> IncrementAsync() => DurableTask.FromResult(++unrelated.Value);

    public DurableTask<int> ComputeAsync(int value) => DurableTask.FromResult(value);

    public Task<AttemptCancellationSnapshot> GetSnapshotAsync() => Task.FromResult(new AttemptCancellationSnapshot(
        status.Value,
        unrelated.Value,
        dictionary.Count + list.Count + queue.Count + set.Count + log.Count
            + (completion.State.Status is DurableTaskCompletionSourceStatus.Pending ? 0 : 1)
            + (state.RecordExists ? 1 : 0)));

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private async Task CallExternalServiceAsync(GrainId grainId, CancellationToken cancellationToken)
    {
        probe.Increment(grainId, AttemptCancellationProbe.ExternalCalls);
        await probe.WaitAsync(grainId, AttemptCancellationProbe.Release, cancellationToken);
    }

    private async Task TryCanceledAttemptOperationsAsync(GrainId grainId)
    {
        var other = GrainFactory.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var deadline = DurableTask.Current.UtcNow.AddMinutes(1);
        Try("once", () => DurableTask.Once("cleanup", static () => { }));
        await TryAsync("child", async () => await DurableTask.Run(static _ => { }).WithId("child"));
        await TryAsync("shielded-child", async () => await DurableTask.Run(static _ => { }).WithId("shielded-child").Shielded());
        await TryAsync("detached-call", async () => await other.ComputeAsync(1).Detached().ScheduleAsync());
        await TryAsync("call", async () => await other.ComputeAsync(1));
        Try("post", () => DurableTask.Run(static _ => { }).WithId("posted").Post());
        await TryAsync("schedule", async () => await DurableTask.Run(static _ => { }).WithId("scheduled").ScheduleAsync());
        await TryAsync("delay", async () => await DurableTask.Delay(TimeSpan.FromMinutes(1)));
        await TryAsync("signal-wait", async () => await Approved.WaitAsync());
        await TryAsync("signal-send", async () => await other.SignalDurableTaskAsync("other", Approved, "approved"));
        await TryAsync("subscription", async () => await other.GetDurableTask<int>("other"));
        await TryAsync("when-all", async () => await DurableTask.WhenAll([DurableTask.Run(static _ => { })]));
        await TryAsync("when-any", async () => await DurableTask.WhenAny([DurableTask.Run(static _ => { })]));
        await TryAsync("race", async () => await DurableTask.Race([DurableTask.Run(static _ => { })]));
        await TryAsync("timeout", async () => await DurableTask.Run(static _ => { }).WaitAsync(TimeSpan.FromMinutes(1)));
        await TryAsync("try-wait", async () => await Approved.TryWaitAsync(deadline));
        Try("value", () => status.Value = "cleaned up");
        Try("dictionary", () => dictionary.Add("cleaned up", 1));
        Try("list", () => list.Add("cleaned up"));
        Try("queue", () => queue.Enqueue("cleaned up"));
        Try("set", () => set.Add("cleaned up"));
        Try("log", () => log.Append("cleaned up"));
        Try("completion", () => completion.TrySetResult(1));
        Try("persistent-state", () => state.State = "cleaned up");
        await TryAsync("persistent-state-write", () => state.WriteStateAsync());
        await TryAsync("grain-write", async () => await WriteStateAsync());

        async Task TryAsync(string operation, Func<Task> action) =>
            probe.Record(grainId, operation, await global::Xunit.Record.ExceptionAsync(action));

        void Try(string operation, Action action) => probe.Record(grainId, operation, Record(action));
    }

    private static Exception? Record(Action action) => global::Xunit.Record.Exception(action);

    private sealed class ProbeResource(AttemptCancellationProbe probe, GrainId grainId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            probe.Record(grainId, AttemptCancellationProbe.ResourceDisposed, null);
            return ValueTask.CompletedTask;
        }
    }
}
