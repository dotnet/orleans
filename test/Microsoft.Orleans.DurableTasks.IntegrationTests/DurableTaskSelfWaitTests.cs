using Orleans.Concurrency;
using Orleans.Durable.Tasks;
using Orleans.Durable.Tasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that a request which waits for a task of its own grain, which the grain's admission rules hold the task's
/// segments behind, fails at once, and that waits which the grain admits the segments alongside complete.
/// </summary>
public abstract class DurableTaskSelfWaitTests(string journalFormatKey) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task WaitForAnOwnTaskThroughAGrainReferenceFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.ScheduleAndAwaitOwnTaskAsync(2).WaitAsync(Timeout, cancellationToken));

        Assert.Contains("[AlwaysInterleave]", exception.Message, StringComparison.Ordinal);

        // The task itself runs once the failed request has released the grain.
        Assert.Equal(6, (await grain.GetDurableTask<int>("own").GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<int>());
    }

    [Fact]
    public async Task WaitForAnOwnTaskThroughTheTaskManagerFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.CancelAndAwaitThroughManagerAsync("wait").WaitAsync(Timeout, cancellationToken));

        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
    }

    [Fact]
    public async Task WaitForAnOwnTaskThroughATaskReferenceFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.AwaitOwnTaskReferenceAsync("wait").WaitAsync(Timeout, cancellationToken));
        Assert.Contains("[AlwaysInterleave]", exception.Message, StringComparison.Ordinal);

        await waiting.CancelAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
    }

    [Fact]
    public async Task InterleavingWaitForAnOwnTaskThroughATaskReferenceCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, await grain.InterleavingCancelAndAwaitOwnTaskReferenceAsync("wait").WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken)).Status);
    }

    [Fact]
    public async Task WaitForAnOwnTaskThroughATaskReferenceWhichAClientPolledFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        // The client polls the reference first, then the grain awaits the same reference, and then the client uses it again.
        var reference = grain.GetDurableTask<int>("wait");
        Assert.Equal(DurableTaskStatus.Pending, await reference.GetStatusAsync(cancellationToken: cancellationToken));
        SelfWaitingGrain.ShareReference(key, reference);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.AwaitSharedReferenceAsync().WaitAsync(Timeout, cancellationToken));
        Assert.Contains("[AlwaysInterleave]", exception.Message, StringComparison.Ordinal);

        await reference.CancelAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, (await reference.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
    }

    [Fact]
    public async Task WaitForAnOwnDelayCompletesOnceItIsDue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.DelayAsync().WithId("workflow").ScheduleAsync(cancellationToken);
        var delayId = await SelfWaitingGrain.WaitUntilDelayScheduledAsync(key).WaitAsync(Timeout, cancellationToken);

        // Inbox delivery completes the delay alongside the waiting request, without running the workflow's segments.
        var waiter = grain.AwaitTaskThroughManagerAsync(delayId);
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        _fixture.JobClock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await waiter.WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task WaitForAnOwnInvokedDelayCompletesOnceItIsDue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.SleepAsync().WithId("sleep").ScheduleAsync(cancellationToken);
        var sleepId = TaskId.CreateRoot("sleep");

        // The task's body is the delay, which records its due time once the body's segment runs.
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            ready.CancelAfter(Timeout);
            while (!await HasDueTimeAsync(grain, sleepId, ready.Token))
            {
            }
        }

        // Inbox delivery completes the delay alongside the waiting request, without another segment.
        var waiter = grain.AwaitTaskThroughManagerAsync(sleepId);
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        _fixture.JobClock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await waiter.WaitAsync(Timeout, cancellationToken));

        static async Task<bool> HasDueTimeAsync(IAddressable grain, TaskId taskId, CancellationToken cancellationToken)
        {
            await foreach (var (id, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync(cancellationToken))
            {
                if (id == taskId)
                {
                    return state.DueTime is not null;
                }
            }

            return false;
        }
    }

    [Fact]
    public async Task DurableBodyWhichWaitsForAnOwnTaskThroughTheTaskManagerFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        // The body waits directly, not at a durable wait, so its own segment holds the other task's segments back.
        var body = await grain.AwaitThroughManagerFromBodyAsync("wait").WithId("body").ScheduleAsync(cancellationToken);
        var response = await body.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Contains("[AlwaysInterleave]", Assert.IsType<InvalidOperationException>(response.Exception).Message, StringComparison.Ordinal);

        await waiting.CancelAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
    }

    [Fact]
    public async Task DurableBodyWhichWaitsForItsNewDelayThroughTheTaskManagerFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        // The delay records its due time only once its first segment runs, which the waiting body's own segment holds back.
        var body = await grain.AwaitNewDelayThroughManagerFromBodyAsync().WithId("body").ScheduleAsync(cancellationToken);
        var response = await body.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.IsType<InvalidOperationException>(response.Exception);
    }

    [Fact]
    public async Task WaitForAnOwnTaskWhoseAbortIsStagedCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        // The abort stages the task's terminal result, which its journal write completes without the task's segments.
        Assert.Equal(DurableTaskStatus.Failed, await grain.AbortAndAwaitThroughManagerAsync("wait").WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableTaskStatus.Failed, (await waiting.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
    }

    [Fact]
    public async Task StatusPollsForAnOwnPendingTaskReturnPending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);

        // A poll is bounded, so a request which polls its own grain's pending task observes it as pending.
        Assert.Equal(
            [DurableTaskStatus.Pending, DurableTaskStatus.Pending],
            await grain.PollOwnTaskStatusAsync("wait").WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public Task WaitForAnOwnTaskThroughASubscriptionFailsAtOnce() => AssertWaitThroughChildFailsAtOnce(detached: false);

    [Fact]
    public Task WaitForAnOwnTaskThroughADetachedStartFailsAtOnce() => AssertWaitThroughChildFailsAtOnce(detached: true);

    // The child completes once the grain's own task does, whose segments the waiting request holds back.
    private async Task AssertWaitThroughChildFailsAtOnce(bool detached)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);
        await grain.AwaitOwnTaskThroughChildAsync(detached).WithId("workflow").ScheduleAsync(cancellationToken);
        var childId = await SelfWaitingGrain.WaitUntilChildScheduledAsync(key).WaitAsync(Timeout, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.AwaitTaskThroughManagerAsync(childId).WaitAsync(Timeout, cancellationToken));
        Assert.Contains("[AlwaysInterleave]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavingWaitsForOwnTasksComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        Assert.Equal(6, await grain.InterleavingScheduleAndAwaitOwnTaskAsync(2).WaitAsync(Timeout, cancellationToken));

        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, await grain.InterleavingCancelAndAwaitThroughManagerAsync("wait").WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken)).Status);
    }

    [Fact]
    public async Task WaitsForOwnTasksOnAReentrantGrainComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IReentrantSelfWaitingGrain>(Guid.NewGuid());

        Assert.Equal(6, await grain.ScheduleAndAwaitOwnTaskAsync(2).WaitAsync(Timeout, cancellationToken));

        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, await grain.CancelAndAwaitThroughManagerAsync("wait").WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken)).Status);
    }

    [Fact]
    public async Task DurableBodyAwaitsADurableCallToItsOwnGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        var outer = await grain.AwaitOwnCallAsync(2).WithId("outer").ScheduleAsync(cancellationToken);
        Assert.Equal(7, await outer.WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task InterleavingWaiterAlongsideAnotherBlockingRequestCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        var blocking = grain.BlockAsync();
        await SelfWaitingGrain.WaitUntilBlockedAsync(key).WaitAsync(Timeout, cancellationToken);

        // The other request holds the task's segments, not the interleaving waiter, so the waiter waits for that request.
        var waiter = grain.InterleavingScheduleAndAwaitOwnTaskAsync(2);
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        SelfWaitingGrain.Release(key);
        await blocking.WaitAsync(Timeout, cancellationToken);
        Assert.Equal(6, await waiter.WaitAsync(Timeout, cancellationToken));
    }
}

/// <summary>Runs self-wait coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskSelfWaitTests() : DurableTaskSelfWaitTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs self-wait coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskSelfWaitTests() : DurableTaskSelfWaitTests(DurableTaskRecoveryFixture.OrleansBinary);

public interface ISelfWaitingGrain : IGrainWithGuidKey
{
    DurableTask<int> TripleAsync(int value);

    DurableTask<int> AwaitOwnCallAsync(int value);

    DurableTask<int> WaitForCancellationAsync();

    Task<int> ScheduleAndAwaitOwnTaskAsync(int value);

    Task<DurableTaskStatus> CancelAndAwaitThroughManagerAsync(string rootId);

    Task<int> AwaitOwnTaskReferenceAsync(string rootId);

    Task<int> AwaitSharedReferenceAsync();

    DurableTask DelayAsync();

    DurableTask SleepAsync();

    Task<DurableTaskStatus> AwaitTaskThroughManagerAsync(TaskId taskId);

    Task<DurableTaskStatus> AbortAndAwaitThroughManagerAsync(string rootId);

    Task<DurableTaskStatus[]> PollOwnTaskStatusAsync(string rootId);

    DurableTask<int> AwaitOwnTaskThroughChildAsync(bool detached);

    DurableTask<DurableTaskStatus> AwaitThroughManagerFromBodyAsync(string rootId);

    DurableTask<DurableTaskStatus> AwaitNewDelayThroughManagerFromBodyAsync();

    [AlwaysInterleave]
    Task<DurableTaskStatus> InterleavingCancelAndAwaitOwnTaskReferenceAsync(string rootId);

    [AlwaysInterleave]
    Task<int> InterleavingScheduleAndAwaitOwnTaskAsync(int value);

    [AlwaysInterleave]
    Task<DurableTaskStatus> InterleavingCancelAndAwaitThroughManagerAsync(string rootId);

    Task BlockAsync();
}

public interface IReentrantSelfWaitingGrain : IGrainWithGuidKey
{
    DurableTask<int> TripleAsync(int value);

    DurableTask<int> WaitForCancellationAsync();

    Task<int> ScheduleAndAwaitOwnTaskAsync(int value);

    Task<DurableTaskStatus> CancelAndAwaitThroughManagerAsync(string rootId);
}

[GrainType("durable-task-self-waiting-test")]
public sealed class SelfWaitingGrain(IDurableTaskManager tasks) : DurableGrain, ISelfWaitingGrain
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (TaskCompletionSource Blocked, TaskCompletionSource Release)> Gates = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DurableTaskReference<int>> SharedReferences = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<TaskId>> ChildGates = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<TaskId>> DelaysScheduled = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> WaitsStarted = new();

    public static Task WaitUntilBlockedAsync(Guid key) => GetGate(key).Blocked.Task;

    /// <summary>Completes once one of the grain's waiters has started waiting for a task.</summary>
    public static Task WaitUntilWaitStartedAsync(Guid key) => GetWaitStarted(key).Task;

    private static TaskCompletionSource GetWaitStarted(Guid key) =>
        WaitsStarted.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Hands <paramref name="reference"/>, which another caller has used, to the grain's <c>AwaitSharedReferenceAsync</c>.</summary>
    public static void ShareReference(Guid key, DurableTaskReference<int> reference) => SharedReferences[key] = reference;

    /// <summary>Completes with the identifier of the child which the grain's <c>AwaitOwnTaskThroughChildAsync</c> recorded.</summary>
    public static Task<TaskId> WaitUntilChildScheduledAsync(Guid key) =>
        ChildGates.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>Completes with the identifier of the delay which the grain's <c>DelayAsync</c> scheduled.</summary>
    public static Task<TaskId> WaitUntilDelayScheduledAsync(Guid key) => GetDelayScheduled(key).Task;

    private static TaskCompletionSource<TaskId> GetDelayScheduled(Guid key) =>
        DelaysScheduled.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void Release(Guid key) => GetGate(key).Release.TrySetResult();

    private static (TaskCompletionSource Blocked, TaskCompletionSource Release) GetGate(Guid key) =>
        Gates.GetOrAdd(key, _ => (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)));

    public DurableTask<int> TripleAsync(int value) => DurableTask.FromResult(value * 3);

    // A durable call to the grain itself is a detached root, whose result the body awaits at a durable wait.
    public async DurableTask<int> AwaitOwnCallAsync(int value) =>
        await this.AsReference<ISelfWaitingGrain>().TripleAsync(value).Detached("own-call") + 1;

    public DurableTask<int> WaitForCancellationAsync() => SelfWaiting.WaitForCancellationAsync();

    public Task<int> ScheduleAndAwaitOwnTaskAsync(int value) => SelfWaiting.ScheduleAndAwaitAsync(this.AsReference<ISelfWaitingGrain>().TripleAsync(value));

    public Task<DurableTaskStatus> CancelAndAwaitThroughManagerAsync(string rootId) => SelfWaiting.CancelAndAwaitAsync(tasks, rootId);

    public async Task<int> AwaitOwnTaskReferenceAsync(string rootId) =>
        await this.AsReference<ISelfWaitingGrain>().GetDurableTask<int>(rootId);

    public async Task<int> AwaitSharedReferenceAsync() => await SharedReferences[this.GetPrimaryKey()];

    public DurableTask SleepAsync() => DurableTask.Delay(TimeSpan.FromHours(1));

    public async DurableTask DelayAsync()
    {
        var delay = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
        GetDelayScheduled(this.GetPrimaryKey()).TrySetResult(delay.Id);
        await delay;
    }

    public async Task<DurableTaskStatus> AwaitTaskThroughManagerAsync(TaskId taskId)
    {
        var waiting = tasks.GetTask(taskId).GetResponseAsync();
        GetWaitStarted(this.GetPrimaryKey()).TrySetResult();
        return (await waiting).Status;
    }

    public async Task<DurableTaskStatus> AbortAndAwaitThroughManagerAsync(string rootId)
    {
        var taskId = TaskId.CreateRoot(rootId);
        var abort = tasks.AbortAsync(taskId, "aborted while a request waits");
        var status = (await tasks.GetTask(taskId).GetResponseAsync()).Status;
        await abort;
        return status;
    }

    public async Task<DurableTaskStatus[]> PollOwnTaskStatusAsync(string rootId)
    {
        var options = new PollingOptions { PollTimeout = TimeSpan.FromMilliseconds(100) };
        return
        [
            await tasks.GetTask(TaskId.CreateRoot(rootId)).GetStatusAsync(options),
            await this.AsReference<ISelfWaitingGrain>().GetDurableTask<int>(rootId).GetStatusAsync(options),
        ];
    }

    // A misuse which replay can't record: the body waits through the task manager instead of at a durable wait.
    public async DurableTask<DurableTaskStatus> AwaitThroughManagerFromBodyAsync(string rootId) =>
        (await tasks.GetTask(TaskId.CreateRoot(rootId)).GetResponseAsync()).Status;

    public async DurableTask<DurableTaskStatus> AwaitNewDelayThroughManagerFromBodyAsync()
    {
        var delay = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
        return (await tasks.GetTask(delay.Id).GetResponseAsync()).Status;
    }

    // The execution subscribes to the grain's own task "wait", or starts a detached task on the grain, and waits for it.
    public async DurableTask<int> AwaitOwnTaskThroughChildAsync(bool detached)
    {
        var self = this.AsReference<ISelfWaitingGrain>();
        var child = detached
            ? await self.WaitForCancellationAsync().Detached("detached").ScheduleAsync()
            : await self.GetDurableTask<int>("wait").WithId("sub").ScheduleAsync();
        ChildGates.GetOrAdd(this.GetPrimaryKey(), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(child.Id);
        return await child;
    }

    public async Task<DurableTaskStatus> InterleavingCancelAndAwaitOwnTaskReferenceAsync(string rootId)
    {
        var reference = this.AsReference<ISelfWaitingGrain>().GetDurableTask<int>(rootId);
        await reference.CancelAsync();
        return (await reference.GetResponseAsync()).Status;
    }

    public async Task<int> InterleavingScheduleAndAwaitOwnTaskAsync(int value)
    {
        var scheduled = await this.AsReference<ISelfWaitingGrain>().TripleAsync(value).ScheduleAsync();
        GetWaitStarted(this.GetPrimaryKey()).TrySetResult();
        return await scheduled;
    }

    public Task<DurableTaskStatus> InterleavingCancelAndAwaitThroughManagerAsync(string rootId) => SelfWaiting.CancelAndAwaitAsync(tasks, rootId);

    public async Task BlockAsync()
    {
        var gate = GetGate(this.GetPrimaryKey());
        gate.Blocked.TrySetResult();
        await gate.Release.Task;
    }
}

[Reentrant]
[GrainType("durable-task-reentrant-self-waiting-test")]
public sealed class ReentrantSelfWaitingGrain(IDurableTaskManager tasks) : DurableGrain, IReentrantSelfWaitingGrain
{
    public DurableTask<int> TripleAsync(int value) => DurableTask.FromResult(value * 3);

    public DurableTask<int> WaitForCancellationAsync() => SelfWaiting.WaitForCancellationAsync();

    public Task<int> ScheduleAndAwaitOwnTaskAsync(int value) => SelfWaiting.ScheduleAndAwaitAsync(this.AsReference<IReentrantSelfWaitingGrain>().TripleAsync(value));

    public Task<DurableTaskStatus> CancelAndAwaitThroughManagerAsync(string rootId) => SelfWaiting.CancelAndAwaitAsync(tasks, rootId);
}

internal static class SelfWaiting
{
    private static readonly DurableSignal<int> Never = new("never");

    // The task parks at a durable signal which never arrives, so only its cancellation ends it.
    public static async DurableTask<int> WaitForCancellationAsync()
    {
        await Never.WaitAsync();
        return 0;
    }

    public static async Task<int> ScheduleAndAwaitAsync(DurableTask<int> task, string? name = "own")
    {
        var scheduled = await (name is null ? task.ScheduleAsync() : task.WithId(name).ScheduleAsync());
        return await scheduled;
    }

    public static async Task<DurableTaskStatus> CancelAndAwaitAsync(IDurableTaskManager tasks, string rootId)
    {
        var taskId = TaskId.CreateRoot(rootId);
        await tasks.CancelAsync(taskId);
        return (await tasks.GetTask<int>(taskId).GetResponseAsync()).Status;
    }
}
