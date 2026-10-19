using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
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
/// Tests that a canceled attempt of a durable task unwinds: its durable operations throw and its cleanup's state changes
/// commit, while task cancellation records its compensation.
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
    public async Task DeactivationDiscardsTheParkedAttemptWithoutRunningItsCleanup()
    {
        const string rootId = "deactivated";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var scheduled = await grain.WaitForCancellationAsync().WithId(rootId).ScheduleAsync(cancellationToken);
        await Probe.WaitAsync(grainId, AttemptCancellationProbe.Waiting, cancellationToken);
        Assert.True(_fixture.Cluster.TryGetGrainContext(grainId, out var activation));

        // The attempt is parked on its signal, so deactivation discards it: it isn't resumed, so its cleanup, which isn't
        // part of the execution's history, never runs.
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(0, Probe.Get(grainId, AttemptCancellationProbe.Catches));
        Assert.False(Probe.GetOutcomes(grainId).ContainsKey(AttemptCancellationProbe.Caught));

        // The next activation replays the task, which assigns its status again and completes once it receives its signal
        // without running its effect again.
        Assert.Equal(new AttemptCancellationSnapshot(Status: "running", Unrelated: 0), await grain.GetSnapshotAsync());
        await grain.SignalDurableTaskAsync(rootId, AttemptCancellationTestGrain.Approved, "approved", cancellationToken);
        Assert.Equal("approved", (await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).GetResult<string>());
        Assert.Equal(new AttemptCancellationSnapshot(Status: "running", Unrelated: 0), await grain.GetSnapshotAsync());
        Assert.Equal(1, Probe.Get(grainId, AttemptCancellationProbe.EffectRuns));
        Assert.Equal(0, Probe.Get(grainId, AttemptCancellationProbe.Catches));
    }

    [Fact]
    public async Task CancellationReachesAStepWhoseSegmentHoldsTheGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IAttemptCancellationTestGrain>(Guid.NewGuid());
        var scheduled = await grain.BlockedStepAsync().WithId("blocked").ScheduleAsync(cancellationToken);
        await Probe.WaitAsync(grain.GetGrainId(), AttemptCancellationProbe.Waiting, cancellationToken);

        // The step's segment holds the non-reentrant grain, and the cancellation, which only stages task state, interleaves
        // with it, so the step's token observes it.
        await scheduled.CancelAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal("canceled", response.GetResult<string>());
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
        var cleanedUp = new AttemptCancellationSnapshot(Status: "cleaned up", Unrelated: 1, AttemptCancellationTestGrain.CleanupChanges);
        Assert.Equal(cleanedUp, await grain.GetSnapshotAsync());

        // The committed journal holds the same state, including the aborted attempt's cleanup.
        Assert.True(_fixture.Cluster.TryGetGrainContext(grainId, out var activation));
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(cleanedUp, await grain.GetSnapshotAsync());
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

        // The compensation's external call observes the attempt's cancellation, and the state change which follows it
        // succeeds.
        await grain.RequestDeactivationAsync();
        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var interrupted = Probe.GetOutcomes(grainId);
        Assert.IsType<TaskCanceledException>(interrupted["external-call"]);
        Assert.Null(interrupted["after-external-call"]);

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

        // The cleanup's changes to journaled state succeed.
        foreach (var operation in AttemptCancellationTestGrain.CleanupOperations)
        {
            Assert.Null(outcomes[operation]);
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

    /// <summary>Runs a step which waits on its token without a durable wait, so its segment holds the grain.</summary>
    DurableTask<string> BlockedStepAsync();

    DurableTask<int> ComputeAsync(int value);

    Task<AttemptCancellationSnapshot> GetSnapshotAsync();

    /// <summary>Deactivates the grain, interleaving with an execution whose segment runs.</summary>
    [AlwaysInterleave]
    Task RequestDeactivationAsync();
}

/// <summary>The grain's journaled state which a canceled attempt's cleanup changes.</summary>
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

    /// <summary>The durable operations which a canceled attempt's code tries, each of which throws the attempt's cancellation.</summary>
    public static readonly string[] CanceledAttemptOperations =
    [
        "once", "child", "shielded-child", "detached-call", "call", "post", "schedule", "delay", "signal-wait",
        "signal-send", "subscription", "when-all", "when-any", "race", "timeout", "try-wait",
    ];

    /// <summary>The journaled state changes which a canceled attempt's cleanup makes, each of which succeeds.</summary>
    public static readonly string[] CleanupOperations =
    [
        "value", "dictionary", "list", "queue", "set", "log", "completion", "persistent-state", "persistent-state-write",
        "grain-write",
    ];

    /// <summary>
    /// The number of changes which the cleanup adds to <see cref="AttemptCancellationSnapshot.Changes"/>: an entry in each
    /// collection, the completion's result, and the persistent state's record.
    /// </summary>
    public const int CleanupChanges = 7;

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async DurableTask<string> WaitForCancellationAsync()
    {
        var context = DurableTask.Current;
        var grainId = this.GetGrainId();
        DurableTask.RunInline("started", () => probe.Increment(grainId, AttemptCancellationProbe.EffectRuns));
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
            DurableTask.RunInline("compensate", () => probe.Increment(grainId, AttemptCancellationProbe.Compensations));
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

    public DurableTask<string> BlockedStepAsync()
    {
        var grainId = this.GetGrainId();
        return DurableTask.Run(async cancellationToken =>
        {
            probe.Signal(grainId, AttemptCancellationProbe.Waiting);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "completed";
            }
            catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
            {
                return "canceled";
            }
        });
    }

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
        Try("once", () => DurableTask.RunInline("cleanup", static () => { }));
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
