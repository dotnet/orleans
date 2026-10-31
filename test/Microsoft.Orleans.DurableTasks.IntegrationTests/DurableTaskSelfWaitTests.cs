using Orleans.Concurrency;
using Orleans.Durable.Tasks;
using Orleans.Durable.Tasks.Protocol;
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
    public async Task WaitForAnOwnTaskFromOnActivateAsyncFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        ActivationSelfWaitingGrain.WaitOnNextActivation(key);
        var grain = _fixture.Client.GetGrain<IActivationSelfWaitingGrain>(key);

        // The grain runs the task's segments only once it has activated, so a wait from its activation code can't end.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.PingAsync().WaitAsync(Timeout, cancellationToken));
        Assert.Contains("OnActivateAsync", exception.Message, StringComparison.Ordinal);

        // The task itself runs once a later activation completes.
        Assert.Equal(6, (await grain.GetDurableTask<int>("own").GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).GetResult<int>());
    }

    [Fact]
    public async Task WaitForAnOwnTaskReferenceFromOnActivateAsyncFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<IActivationSelfWaitingGrain>(key);
        var waiting = await grain.WaitForCancellationAsync().WithId("wait").ScheduleAsync(cancellationToken);
        await grain.DeactivateAsync().WaitAsync(Timeout, cancellationToken);
        ActivationSelfWaitingGrain.WaitForReferenceOnNextActivation(key, "wait");

        // The next activation's code awaits a reference to the existing task, whose segments run only once it activates.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.PingAsync().WaitAsync(Timeout, cancellationToken));
        Assert.Contains("OnActivateAsync", exception.Message, StringComparison.Ordinal);

        await waiting.CancelAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.Canceled, (await waiting.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken)).Status);
    }

    [Fact]
    public async Task WaitForAnOwnRemoteCallFromOnActivateAsyncFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<IActivationSelfWaitingGrain>(key);
        await grain.CallAnotherAsync(Guid.NewGuid()).WithId("workflow").ScheduleAsync(cancellationToken);
        var callId = await ActivationSelfWaitingGrain.WaitUntilCallScheduledAsync(key).WaitAsync(Timeout, cancellationToken);
        await grain.DeactivateAsync().WaitAsync(Timeout, cancellationToken);
        ActivationSelfWaitingGrain.WaitForTaskOnNextActivation(key, callId);

        // The next activation's code waits for the recovered call, whose result the grain's inbox delivers only once it activates.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.PingAsync().WaitAsync(Timeout, cancellationToken));
        Assert.Contains("OnActivateAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForAMissingOwnTaskFromOnActivateAsyncReportsItMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        ActivationSelfWaitingGrain.WaitForReferenceOnNextActivation(key, "missing");
        var grain = _fixture.Client.GetGrain<IActivationSelfWaitingGrain>(key);

        // The grain holds no such task, so the reference's lookup reports it, which activation code can handle.
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(() => grain.PingAsync().WaitAsync(Timeout, cancellationToken));
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
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.OpenSectionAsync().WaitAsync(Timeout, cancellationToken);

        // The waiter's section waits behind the open one, and the waiter holds it once the open one closes.
        var waiter = grain.InterleavingScheduleAndAwaitOwnTaskInSectionAsync(2, holdSection: true);
        await SelfWaitingGrain.WaitUntilSectionRequestedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        await grain.CloseSectionAsync().WaitAsync(Timeout, cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter.WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task InterleavingWaitForAnOwnTaskBehindAnotherFlowsSectionCompletesOnceItCloses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.OpenSectionAsync().WaitAsync(Timeout, cancellationToken);

        // Another flow's section holds the task's segments back, and it closes without the waiter, so the waiter waits.
        var waiter = grain.InterleavingScheduleAndAwaitOwnTaskInSectionAsync(2, holdSection: false);
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        await grain.CloseSectionAsync().WaitAsync(Timeout, cancellationToken);
        Assert.Equal(6, await waiter.WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task CallbackOnTheSectionHoldersCallChainWhichWaitsForAnOwnTaskFailsAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(Guid.NewGuid());

        // The holder waits for another grain, which calls back on the holder's call chain, so the section stays open while
        // the callback waits for a task of the grain, whose segments the section holds back.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.OpenSectionAndCallBackAsync(Guid.NewGuid()).WaitAsync(Timeout, cancellationToken));
        Assert.Contains("exclusive section", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuationWhichOutlivesItsCallbackWaitsForTheSectionToClose()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);

        // The callback returns, so the holder no longer waits for it, and its continuation waits for the section to close.
        var holder = grain.OpenSectionAndStartContinuationAsync(Guid.NewGuid());
        await SelfWaitingGrain.WaitUntilHolderWaitingAsync(key).WaitAsync(Timeout, cancellationToken);
        SelfWaitingGrain.ReleaseContinuation(key);
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(holder.IsCompleted);

        SelfWaitingGrain.ReleaseHolder(key);
        await holder.WaitAsync(Timeout, cancellationToken);
        Assert.Equal(6, await SelfWaitingGrain.WaitForContinuationAsync(key).WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task InterleavingWaitInsideItsOwnSectionCompletesWhenTheTasksRunningSegmentCompletesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.HoldSegmentAsync(park: false).WithId("held").ScheduleAsync(cancellationToken);
        await SelfWaitingGrain.WaitUntilSegmentHeldAsync(key).WaitAsync(Timeout, cancellationToken);

        // The task's segment runs before the waiter opens its section, so it continues within the section.
        var waiter = grain.InterleavingAwaitInOwnSectionThroughManagerAsync("held");
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        SelfWaitingGrain.ReleaseSegment(key);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await waiter.WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task InterleavingWaitInsideItsOwnSectionFailsWhenTheTasksRunningSegmentParks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.HoldSegmentAsync(park: true).WithId("held").ScheduleAsync(cancellationToken);
        await SelfWaitingGrain.WaitUntilSegmentHeldAsync(key).WaitAsync(Timeout, cancellationToken);

        // Once the running segment parks the task, its next segment waits for the section which the waiter holds.
        var waiter = grain.InterleavingAwaitInOwnSectionThroughManagerAsync("held");
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        SelfWaitingGrain.ReleaseSegment(key);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => waiter.WaitAsync(Timeout, cancellationToken));
        Assert.Contains("exclusive section", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavingWaitInsideItsOwnSectionCompletesWhenTheTaskIsAbortedWhileItsSegmentRuns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var key = Guid.NewGuid();
        var grain = _fixture.Client.GetGrain<ISelfWaitingGrain>(key);
        await grain.HoldSegmentAsync(park: false).WithId("held").ScheduleAsync(cancellationToken);
        await SelfWaitingGrain.WaitUntilSegmentHeldAsync(key).WaitAsync(Timeout, cancellationToken);
        var waiter = grain.InterleavingAwaitInOwnSectionThroughManagerAsync("held");
        await SelfWaitingGrain.WaitUntilWaitStartedAsync(key).WaitAsync(Timeout, cancellationToken);
        Assert.False(waiter.IsCompleted);

        // The abort completes the task while its segment still waits for the test, which the waiter observes.
        await grain.GetDurableTask<int>("held").AbortAsync("aborted while its segment runs", cancellationToken);
        Assert.Equal(DurableTaskStatus.Failed, await waiter.WaitAsync(Timeout, cancellationToken));
        SelfWaitingGrain.ReleaseSegment(key);
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

    [AlwaysInterleave]
    Task<int> InterleavingScheduleAndAwaitOwnTaskInSectionAsync(int value, bool holdSection);

    [AlwaysInterleave]
    Task OpenSectionAsync();

    Task<int> OpenSectionAndCallBackAsync(Guid callerKey);

    Task OpenSectionAndStartContinuationAsync(Guid callerKey);

    [AlwaysInterleave]
    Task InterleavingStartContinuationAsync();

    DurableTask<int> HoldSegmentAsync(bool park);

    [AlwaysInterleave]
    Task<DurableTaskStatus> InterleavingAwaitInOwnSectionThroughManagerAsync(string rootId);

    [AlwaysInterleave]
    Task CloseSectionAsync();

    Task BlockAsync();
}

public interface ISelfWaitCallbackGrain : IGrainWithGuidKey
{
    Task<int> CallBackAsync(ISelfWaitingGrain grain);

    Task StartContinuationAsync(ISelfWaitingGrain grain);
}

public interface IActivationSelfWaitingGrain : IGrainWithGuidKey
{
    DurableTask<int> TripleAsync(int value);

    DurableTask<int> WaitForCancellationAsync();

    DurableTask CallAnotherAsync(Guid otherKey);

    Task PingAsync();

    Task DeactivateAsync();
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ContinuationGate> ContinuationGates = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DurableTaskReference<int>> SharedReferences = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<TaskId>> ChildGates = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<TaskId>> DelaysScheduled = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> SectionsRequested = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (TaskCompletionSource Held, TaskCompletionSource Release)> SegmentGates = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> WaitsStarted = new();

    private IDisposable? _section;

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

    /// <summary>Completes once the grain's section-holding waiter has queued its exclusive section request.</summary>
    public static Task WaitUntilSectionRequestedAsync(Guid key) => GetSectionRequested(key).Task;

    private static TaskCompletionSource GetSectionRequested(Guid key) =>
        SectionsRequested.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Completes once the grain's <c>HoldSegmentAsync</c> body holds its segment.</summary>
    public static Task WaitUntilSegmentHeldAsync(Guid key) => GetSegmentGate(key).Held.Task;

    /// <summary>Lets the grain's <c>HoldSegmentAsync</c> body continue.</summary>
    public static void ReleaseSegment(Guid key) => GetSegmentGate(key).Release.TrySetResult();

    private static (TaskCompletionSource Held, TaskCompletionSource Release) GetSegmentGate(Guid key) =>
        SegmentGates.GetOrAdd(key, _ => (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)));

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

    public async Task<int> InterleavingScheduleAndAwaitOwnTaskInSectionAsync(int value, bool holdSection)
    {
        var key = this.GetPrimaryKey();
        using var section = holdSection ? await RequestSectionAsync(key) : null;
        var scheduled = await this.AsReference<ISelfWaitingGrain>().TripleAsync(value).ScheduleAsync();
        var waiting = AwaitAsync(scheduled);
        GetWaitStarted(key).TrySetResult();
        return await waiting;

        static async Task<int> AwaitAsync(ScheduledTask<int> task) => await task;
    }

    // Synchronous, so the section which EnterExclusiveSectionAsync records in the calling flow stays recorded for the waiter.
    private ValueTask<IDisposable> RequestSectionAsync(Guid key)
    {
        var entering = Admission.EnterExclusiveSectionAsync(CancellationToken.None);
        GetSectionRequested(key).TrySetResult();
        return entering;
    }

    // The section stays open after the request completes, as code which isn't a request, such as a transaction, holds it.
    public async Task OpenSectionAsync() => _section = await Admission.EnterExclusiveSectionAsync(CancellationToken.None);

    public async Task<int> OpenSectionAndCallBackAsync(Guid callerKey)
    {
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        using var section = await Admission.EnterExclusiveSectionAsync(CancellationToken.None);
        return await GrainFactory.GetGrain<ISelfWaitCallbackGrain>(callerKey).CallBackAsync(this.AsReference<ISelfWaitingGrain>());
    }

    // The holder keeps its section open after the callback returns, until the test releases it.
    public async Task OpenSectionAndStartContinuationAsync(Guid callerKey)
    {
        var gate = GetContinuationGate(this.GetPrimaryKey());
        using (RequestContext.AllowCallChainReentrancy())
        using (await Admission.EnterExclusiveSectionAsync(CancellationToken.None))
        {
            await GrainFactory.GetGrain<ISelfWaitCallbackGrain>(callerKey).StartContinuationAsync(this.AsReference<ISelfWaitingGrain>());
            gate.HolderWaiting.TrySetResult();
            await gate.ReleaseHolder.Task;
        }
    }

    // The continuation outlives this callback on the grain's scheduler, in the callback's flow, and waits for an own task.
    public Task InterleavingStartContinuationAsync()
    {
        var key = this.GetPrimaryKey();
        var gate = GetContinuationGate(key);
        gate.Continuation = ContinueAsync(this.AsReference<ISelfWaitingGrain>(), gate, key);
        return Task.CompletedTask;

        static async Task<int> ContinueAsync(ISelfWaitingGrain self, ContinuationGate gate, Guid key)
        {
            await gate.ReleaseContinuation.Task;
            var scheduled = await self.TripleAsync(2).ScheduleAsync();
            GetWaitStarted(key).TrySetResult();
            return await scheduled;
        }
    }

    /// <summary>Completes once the grain's continuation holder keeps its section open after its callback returned.</summary>
    public static Task WaitUntilHolderWaitingAsync(Guid key) => GetContinuationGate(key).HolderWaiting.Task;

    /// <summary>Lets the grain's continuation, which outlives its callback, wait for an own task.</summary>
    public static void ReleaseContinuation(Guid key) => GetContinuationGate(key).ReleaseContinuation.TrySetResult();

    /// <summary>Completes with the result of the grain's continuation, which outlived its callback.</summary>
    public static Task<int> WaitForContinuationAsync(Guid key) => GetContinuationGate(key).Continuation!;

    /// <summary>Lets the grain's continuation holder close its section.</summary>
    public static void ReleaseHolder(Guid key) => GetContinuationGate(key).ReleaseHolder.TrySetResult();

    private static ContinuationGate GetContinuationGate(Guid key) => ContinuationGates.GetOrAdd(key, _ => new());

    private sealed class ContinuationGate
    {
        public TaskCompletionSource HolderWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseContinuation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHolder { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int>? Continuation { get; set; }
    }

    // The body's segment waits for the test, then completes the task or parks it at a durable delay.
    public async DurableTask<int> HoldSegmentAsync(bool park)
    {
        var gate = GetSegmentGate(this.GetPrimaryKey());
        gate.Held.TrySetResult();
        await gate.Release.Task;
        if (park)
        {
            await DurableTask.Delay(TimeSpan.FromHours(1));
        }

        return 5;
    }

    public async Task<DurableTaskStatus> InterleavingAwaitInOwnSectionThroughManagerAsync(string rootId)
    {
        using var section = await Admission.EnterExclusiveSectionAsync(CancellationToken.None);
        var waiting = tasks.GetTask(TaskId.CreateRoot(rootId)).GetResponseAsync();
        GetWaitStarted(this.GetPrimaryKey()).TrySetResult();
        return (await waiting).Status;
    }

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

// Calls the section holder back on its call chain, through an interleaving method which waits for a task of the holder.
[GrainType("durable-task-self-wait-callback-test")]
public sealed class SelfWaitCallbackGrain : Grain, ISelfWaitCallbackGrain
{
    public Task<int> CallBackAsync(ISelfWaitingGrain grain) => grain.InterleavingScheduleAndAwaitOwnTaskAsync(2);

    public Task StartContinuationAsync(ISelfWaitingGrain grain) => grain.InterleavingStartContinuationAsync();
}

[GrainType("durable-task-activation-self-waiting-test")]
public sealed class ActivationSelfWaitingGrain(IDurableTaskManager tasks) : DurableGrain, IActivationSelfWaitingGrain
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> WaitingActivations = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> ReferenceWaitingActivations = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskId> TaskWaitingActivations = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<TaskId>> ScheduledCalls = new();

    public static void WaitOnNextActivation(Guid key) => WaitingActivations[key] = true;

    /// <summary>Makes the grain's next activation await a reference to its existing task <paramref name="rootId"/>.</summary>
    public static void WaitForReferenceOnNextActivation(Guid key, string rootId) => ReferenceWaitingActivations[key] = rootId;

    /// <summary>Makes the grain's next activation wait for its task <paramref name="taskId"/> through the task manager.</summary>
    public static void WaitForTaskOnNextActivation(Guid key, TaskId taskId) => TaskWaitingActivations[key] = taskId;

    /// <summary>Completes with the identifier of the call which the grain's <c>CallAnotherAsync</c> scheduled.</summary>
    public static Task<TaskId> WaitUntilCallScheduledAsync(Guid key) => GetScheduledCall(key).Task;

    private static TaskCompletionSource<TaskId> GetScheduledCall(Guid key) =>
        ScheduledCalls.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public DurableTask<int> TripleAsync(int value) => DurableTask.FromResult(value * 3);

    public DurableTask<int> WaitForCancellationAsync() => SelfWaiting.WaitForCancellationAsync();

    // The call's target never completes it, so the call stays pending across activations.
    public async DurableTask CallAnotherAsync(Guid otherKey)
    {
        var call = await GrainFactory.GetGrain<ISelfWaitingGrain>(otherKey).WaitForCancellationAsync().WithId("call").ScheduleAsync();
        GetScheduledCall(this.GetPrimaryKey()).TrySetResult(call.Id);
        await call;
    }

    public Task PingAsync() => Task.CompletedTask;

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        var key = this.GetPrimaryKey();
        if (WaitingActivations.TryRemove(key, out _))
        {
            await SelfWaiting.ScheduleAndAwaitAsync(this.AsReference<IActivationSelfWaitingGrain>().TripleAsync(2));
        }

        if (ReferenceWaitingActivations.TryRemove(key, out var rootId))
        {
            await this.AsReference<IActivationSelfWaitingGrain>().GetDurableTask<int>(rootId);
        }

        if (TaskWaitingActivations.TryRemove(key, out var taskId))
        {
            await tasks.GetTask(taskId).GetResponseAsync(cancellationToken);
        }
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
