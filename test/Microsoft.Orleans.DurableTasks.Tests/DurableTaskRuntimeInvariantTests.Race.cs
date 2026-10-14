using Microsoft.Extensions.Time.Testing;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Races and timeouts over calls to other grains: cancellation of the inputs which lose, and recovery mid-wait.</summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    private const string RaceLostReason = "Another task won the race.";
    private const string PaymentTimeoutReason = "The operation timed out after 00:05:00.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TimeoutCancelsARemoteCallWithItsReasonAndWaitsForTheCallsOutcome(bool callCompletesAnyway) => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, storage, manager, transport) = CreateRuntime(timeProvider: new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var rootId = TaskId.CreateRoot("order");
        var callId = rootId.Child("pay").Child("0");
        var canceling = ObserveCancellation(transport, callId);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, PayWithTimeout));
        await manager.WriteStateAsync(cancellationToken);
        await transport.ResumeSent.WaitAsync(CancellationTestTimeout, cancellationToken);
        var resume = Assert.Single(transport.Resumes);
        Assert.Equal((rootId.Child("pay").Child("1"), DateTimeOffset.UnixEpoch.AddMinutes(5)), (resume.TaskId, resume.DueTime));

        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, resume.TaskId, resume.Generation, resume.DueTime));
        await manager.WriteStateAsync(cancellationToken);
        await canceling.WaitAsync(CancellationTestTimeout, cancellationToken);

        // The callee receives the timeout's reason, and the workflow waits for the call's outcome.
        Assert.Equal(PaymentTimeoutReason, Assert.Single(transport.CancellationReasons));
        Assert.Equal(PaymentTimeoutReason, storage.Get(callId).CancellationReason);
        Assert.Null(storage.Get(rootId).Result);
        runtime.AcceptResponseFromInbox(callId, callCompletesAnyway ? DurableTaskResponse.Completed : DurableTaskResponse.Canceled, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);

        Assert.Equal(callCompletesAnyway ? "paid" : PaymentTimeoutReason, (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
    });

    [Fact]
    public Task RecoveryMidWaitKeepsTheTimeoutsRecordedDueTime() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (runtime, storage, manager, transport) = CreateRuntime(timeProvider: clock);
        var rootId = TaskId.CreateRoot("order");
        var callId = rootId.Child("pay").Child("0");
        var delayId = rootId.Child("pay").Child("1");
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, PayWithTimeout));
        await manager.WriteStateAsync(cancellationToken);
        await transport.ResumeSent.WaitAsync(CancellationTestTimeout, cancellationToken);
        var resume = Assert.Single(transport.Resumes);

        // The activation stops while the workflow waits, and the next one replays the workflow three minutes later.
        await runtime.StopAsync(cancellationToken).WaitAsync(CancellationTestTimeout, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(3));
        var (replacement, _, _, replacementTransport) = CreateRuntime(storage, manager, timeProvider: clock);
        var canceling = ObserveCancellation(replacementTransport, callId);
        await replacement.ResumePendingTasksAsync(cancellationToken);
        await HandleAsync(CreateHandler(replacement), ResumeContext(replacement, resume.TaskId, resume.Generation, resume.DueTime));
        await manager.WriteStateAsync(cancellationToken);
        await canceling.WaitAsync(CancellationTestTimeout, cancellationToken);
        replacement.AcceptResponseFromInbox(callId, DurableTaskResponse.Canceled, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);

        Assert.Equal(PaymentTimeoutReason, (await WaitForResponseAsync(replacement, rootId)).GetResult<string>());
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), resume.DueTime);
        Assert.Empty(replacementTransport.Resumes);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(delayId).Result!.Status);
        Assert.Equal(PaymentTimeoutReason, Assert.Single(replacementTransport.CancellationReasons));
    });

    [Fact]
    public Task RaceCancelsTheLosingRemoteCallsAndWaitsForTheirOutcomes() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("shipping");
        var quoteIds = Enumerable.Range(0, 3).Select(index => rootId.Child("quote").Child($"{index}")).ToArray();
        var invoked = ObserveInvocations(transport, quoteIds.Length);
        var canceling = ObserveCancellations(transport, 2);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
            await DurableTask.Race(quoteIds.Select(static (_, index) => (DurableTask)new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(index + 2))))
                .WithId("quote"))));
        await manager.WriteStateAsync(cancellationToken);
        await invoked.WaitAsync(CancellationTestTimeout, cancellationToken);

        runtime.AcceptResponseFromInbox(quoteIds[1], DurableTaskResponse.Completed, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);
        await canceling.WaitAsync(CancellationTestTimeout, cancellationToken);

        Assert.Equal([quoteIds[0], quoteIds[2]], transport.Cancellations.Select(static cancellation => cancellation.TaskId).OrderBy(static id => id.ToString(), StringComparer.Ordinal));
        Assert.Equal([RaceLostReason, RaceLostReason], transport.CancellationReasons);

        // The race waits until every other call settles: one ends canceled, and the other completes anyway.
        runtime.AcceptResponseFromInbox(quoteIds[0], DurableTaskResponse.Canceled, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);
        Assert.Null(storage.Get(rootId).Result);
        runtime.AcceptResponseFromInbox(quoteIds[2], DurableTaskResponse.Completed, RemoteTarget);
        await manager.WriteStateAsync(cancellationToken);

        Assert.Equal(1, (await WaitForResponseAsync(runtime, rootId)).GetResult<int>());
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(quoteIds[0]).Result!.Status);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(quoteIds[2]).Result!.Status);
    });

    [Fact]
    public Task CancellationOfARaceReachesEveryRemoteCall() => ActivationScheduler.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (runtime, _, manager, transport) = CreateRuntime();
        var rootId = TaskId.CreateRoot("shipping");
        var quoteIds = Enumerable.Range(0, 2).Select(index => rootId.Child("quote").Child($"{index}")).ToArray();
        var invoked = ObserveInvocations(transport, quoteIds.Length);
        var canceling = ObserveCancellations(transport, quoteIds.Length);
        runtime.ScheduleFromInbox(rootId, CreateRequest(1, () => DurableTask.Run(async _ =>
            await DurableTask.Race(quoteIds.Select(static (_, index) => (DurableTask)new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(index + 2))))
                .WithId("quote"))));
        await manager.WriteStateAsync(cancellationToken);
        await invoked.WaitAsync(CancellationTestTimeout, cancellationToken);

        await ((IDurableTaskManager)runtime).CancelAsync(rootId, "buyer", cancellationToken);
        await canceling.WaitAsync(CancellationTestTimeout, cancellationToken);
        Assert.Equal(["buyer", "buyer"], transport.CancellationReasons);
        foreach (var quoteId in quoteIds)
        {
            runtime.AcceptResponseFromInbox(quoteId, DurableTaskResponse.Canceled, RemoteTarget);
        }

        await manager.WriteStateAsync(cancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForResponseAsync(runtime, rootId)).Status);
    });

    private static GrainId RemoteTarget => CreateRemoteRequest(0).Context!.TargetId;

    private static DurableTask<string> PayWithTimeout() => DurableTask.Run(async _ =>
    {
        try
        {
            await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WaitAsync(TimeSpan.FromMinutes(5)).WithId("pay");
            return "paid";
        }
        catch (TimeoutException exception) when (exception.InnerException is OperationCanceledException)
        {
            return exception.Message;
        }
    });

    private static Task ObserveCancellation(RecordingDurableTaskMessageTransport transport, TaskId taskId)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeSendCancellation = (_, _, canceled) =>
        {
            if (canceled == taskId)
            {
                sent.TrySetResult();
            }
        };
        return sent.Task;
    }

    private static Task ObserveCancellations(RecordingDurableTaskMessageTransport transport, int count)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = count;
        transport.BeforeSendCancellation = (_, _, _) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                sent.TrySetResult();
            }
        };
        return sent.Task;
    }

    private static Task ObserveInvocations(RecordingDurableTaskMessageTransport transport, int count)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = count;
        transport.BeforeSendInvocation = (_, _, _, _) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                sent.TrySetResult();
            }
        };
        return sent.Task;
    }
}
