using Orleans.Concurrency;
using Orleans.DurableTasks;
using Orleans.Runtime;
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
    public async Task InterleavingWaitForAnOwnTaskInsideItsOwnExclusiveSectionFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        // The waiter interleaves, but the section it holds admits the task's segments only once it closes.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.InterleavingScheduleAndAwaitOwnTaskInSectionAsync(2, holdSection: true).WaitAsync(Timeout, cancellationToken));
        Assert.Contains("exclusive section", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavingWaitForAnOwnTaskWithoutASectionCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        Assert.Equal(6, await grain.InterleavingScheduleAndAwaitOwnTaskInSectionAsync(2, holdSection: false).WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task InterleavingWaitForAnOwnTaskInsideASectionWhichWaitedForAnotherFailsOnceItOpens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        await grain.OpenSectionAsync().WaitAsync(Timeout, cancellationToken);

        // The waiter's section waits behind the open one, and the waiter holds it once the open one closes.
        var waiter = grain.InterleavingScheduleAndAwaitOwnTaskInSectionAsync(2, holdSection: true);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        Assert.False(waiter.IsCompleted);

        await grain.CloseSectionAsync().WaitAsync(Timeout, cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter.WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task InterleavingWaitForAnOwnTaskBehindAnotherFlowsSectionCompletesOnceItCloses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());
        await grain.OpenSectionAsync().WaitAsync(Timeout, cancellationToken);

        // Another flow's section holds the task's segments back, and it closes without the waiter, so the waiter waits.
        var waiter = grain.InterleavingScheduleAndAwaitOwnTaskAsync(2);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        Assert.False(waiter.IsCompleted);

        await grain.CloseSectionAsync().WaitAsync(Timeout, cancellationToken);
        Assert.Equal(6, await waiter.WaitAsync(Timeout, cancellationToken));
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
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
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

    [AlwaysInterleave]
    Task<int> InterleavingScheduleAndAwaitOwnTaskAsync(int value);

    [AlwaysInterleave]
    Task<DurableTaskStatus> InterleavingCancelAndAwaitThroughManagerAsync(string rootId);

    [AlwaysInterleave]
    Task<int> InterleavingScheduleAndAwaitOwnTaskInSectionAsync(int value, bool holdSection);

    [AlwaysInterleave]
    Task OpenSectionAsync();

    [AlwaysInterleave]
    Task CloseSectionAsync();

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

    private IDisposable? _section;

    public static Task WaitUntilBlockedAsync(Guid key) => GetGate(key).Blocked.Task;

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

    public Task<int> InterleavingScheduleAndAwaitOwnTaskAsync(int value) =>
        SelfWaiting.ScheduleAndAwaitAsync(this.AsReference<ISelfWaitingGrain>().TripleAsync(value), name: null);

    public Task<DurableTaskStatus> InterleavingCancelAndAwaitThroughManagerAsync(string rootId) => SelfWaiting.CancelAndAwaitAsync(tasks, rootId);

    public async Task<int> InterleavingScheduleAndAwaitOwnTaskInSectionAsync(int value, bool holdSection)
    {
        using var section = holdSection ? await Admission.EnterExclusiveSectionAsync(CancellationToken.None) : null;
        return await SelfWaiting.ScheduleAndAwaitAsync(this.AsReference<ISelfWaitingGrain>().TripleAsync(value), name: null);
    }

    // The section stays open after the request completes, as code which isn't a request, such as a transaction, holds it.
    public async Task OpenSectionAsync() => _section = await Admission.EnterExclusiveSectionAsync(CancellationToken.None);

    public Task CloseSectionAsync()
    {
        _section!.Dispose();
        _section = null;
        return Task.CompletedTask;
    }

    private IGrainRequestAdmission Admission => GrainContext.GetComponent<IGrainRequestAdmission>()!;

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
