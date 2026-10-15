using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Verifies signal delivery, latching, waits, and lifecycle in the durable task runtime.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed partial class DurableTaskSignalTests
{
    private static readonly DurableSignal<string> Approved = new("approved");
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");
    private static readonly TaskId RecordId = WorkflowId.Child("$signal:approved");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task SignalBeforeTheWaitIsRecordedAndReturnedByTheWait() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            await gate.Task;
            return await Approved.WaitAsync();
        });

        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);

        var record = harness.Storage.Get(RecordId);
        Assert.Equal("ada", record.Result!.GetResult<string>());
        Assert.StartsWith("$signal:", record.RequestFingerprint, StringComparison.Ordinal);
        gate.SetResult();
        Assert.Equal("ada", await harness.GetResultAsync<string>(WorkflowId));

        // The wait runs inline, so the tree holds only the workflow and the signal record.
        Assert.Equal([WorkflowId, RecordId], harness.Storage.Ids);
        var diagnostics = await harness.GetDiagnosticsAsync();
        Assert.Equal(("Completed", "ada"), (diagnostics[RecordId].Status, diagnostics[RecordId].Response));
    });

    [Fact]
    public Task WaitBeforeTheSignalCompletesWhenTheSignalCommits() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        await harness.ScheduleAsync(WorkflowId, async () => await Approved.WaitAsync());
        await harness.WaitForSignalWaitsAsync(1);

        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Manager.AfterWriteStarted = async () =>
        {
            writeStarted.TrySetResult();
            await releaseWrite.Task;
        };
        var signaling = ((IDurableTaskGrainExtension)harness.Runtime)
            .SignalAsync(WorkflowId, Approved.Name, DurableTaskResponse.FromResult("ada"), TestCancellation).AsTask();
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);

        // The wait observes the signal once it commits.
        Assert.False(signaling.IsCompleted);
        Assert.Equal(DurableTaskStatus.Pending, await harness.PollAsync(WorkflowId));
        harness.Manager.AfterWriteStarted = null;
        releaseWrite.SetResult();
        await signaling;

        Assert.Equal("ada", await harness.GetResultAsync<string>(WorkflowId));
        Assert.Equal(0, harness.SignalWaitCount);
    });

    [Fact]
    public Task EqualValueIsADuplicateAndDifferentValueIsRejected() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            await gate.Task;
            return await Approved.WaitAsync();
        });
        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        var recordedAt = harness.Storage.Get(RecordId).CompletedAt;

        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Tasks.SignalAsync(WorkflowId, Approved, "grace", TestCancellation).AsTask());

        Assert.Contains("different value", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(recordedAt, harness.Storage.Get(RecordId).CompletedAt);
        gate.SetResult();
        Assert.Equal("ada", await harness.GetResultAsync<string>(WorkflowId));

        // A repeated signal with the recorded value succeeds after the task completes.
        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        var terminal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Tasks.SignalAsync(WorkflowId, Approved.For("2"), "ada", TestCancellation).AsTask());
        Assert.Contains("has completed", terminal.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task SignalForATaskWhichIsNotARetainedInvokedTaskIsRejected() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            await DurableTask.Run(async cancellationToken =>
            {
                childStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }).WithId("step");
            return string.Empty;
        });
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);

        var unknown = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => harness.Tasks.SignalAsync(TaskId.CreateRoot("missing"), Approved, "ada", TestCancellation).AsTask());
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => harness.Tasks.SignalAsync(WorkflowId.Child("step"), Approved, "ada", TestCancellation).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => ((IDurableTaskGrainExtension)harness.Runtime).SignalAsync(WorkflowId, Approved.Name, DurableTaskResponse.Pending, TestCancellation).AsTask());

        Assert.Equal(TaskId.CreateRoot("missing"), unknown.TaskId);
        Assert.False(harness.Storage.Contains(RecordId));
        Assert.False(harness.Storage.Contains(WorkflowId.Child("step").Child("$signal:approved")));
        await harness.Runtime.StopAsync(TestCancellation);
    });

    [Fact]
    public Task WaitsInANestedStepAndInAWhenAnySlotObserveTheInvokedTasksSignal() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            var step = await WaitInStep().WithId("step");
            var (index, raced) = await DurableTask.WhenAny([Approved.WaitAsync(), Approved.For("never").WaitAsync()]);
            return $"{step}:{index}:{raced}";
        });
        await harness.WaitForSignalWaitsAsync(1);

        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);

        Assert.Equal("ada:0:ada", await harness.GetResultAsync<string>(WorkflowId));
        Assert.Equal("ada", harness.Storage.Get(WorkflowId.Child("step")).Result!.GetResult<string>());
        Assert.False(harness.Storage.Contains(WorkflowId.Child("step").Child("$signal:approved")));
        Assert.True(harness.Storage.Contains(TaskId.Parse("workflow/$when-any-1/1")));

        static async DurableTask<string> WaitInStep() => await Approved.WaitAsync();
    });

    [Fact]
    public Task WhenAnyRacesTheSignalWithADeadline() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = new Harness(clock);
        var deadline = DateTimeOffset.UnixEpoch.AddHours(1);
        await harness.ScheduleAsync(WorkflowId, () => WaitWithDeadline(deadline));
        var resume = await harness.WaitForResumeAsync();

        Assert.Equal(deadline, resume.DueTime);
        harness.Runtime.ResumeDelayFromInbox(resume.TaskId, resume.Generation, harness.Runtime.GrainId);
        await harness.Manager.WriteStateAsync(TestCancellation);

        Assert.Equal("timed out", await harness.GetResultAsync<string>(WorkflowId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Tasks.SignalAsync(WorkflowId, Approved, "late", TestCancellation).AsTask());

        var signaled = new Harness(clock);
        await signaled.ScheduleAsync(WorkflowId, () => WaitWithDeadline(deadline));
        await signaled.WaitForSignalWaitsAsync(1);
        await signaled.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        Assert.Equal("ada", await signaled.GetResultAsync<string>(WorkflowId));

        static async DurableTask<string> WaitWithDeadline(DateTimeOffset deadline)
        {
            var index = await DurableTask.WhenAny([Approved.WaitAsync(), DurableTask.DelayUntil(deadline)]);
            return index == 0 ? await Approved.WaitAsync() : "timed out";
        }
    });

    [Fact]
    public Task WaitAdvancesLogicalTimeToWhenTheSignalWasRecorded() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = new Harness(clock);
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            await Approved.WaitAsync();
            return DurableTask.Current.UtcNow.ToString("O");
        });
        await harness.WaitForSignalWaitsAsync(1);

        clock.Advance(TimeSpan.FromHours(3));
        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(3).ToString("O"), await harness.GetResultAsync<string>(WorkflowId));
    });

    [Fact]
    public Task ReplayObservesTheRecordedValue() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var observed = new ConcurrentQueue<string>();
        var firstAttemptWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<DurableTask<string>> workflow = async () =>
        {
            var value = await Approved.WaitAsync();
            observed.Enqueue(value);
            if (observed.Count == 1)
            {
                firstAttemptWaiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, DurableTask.Current.CancellationToken);
            }

            return value;
        };
        await harness.ScheduleAsync(WorkflowId, workflow);
        await harness.WaitForSignalWaitsAsync(1);
        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        await firstAttemptWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);

        await harness.Runtime.StopAsync(TestCancellation);
        var replacement = harness.CreateReplacement();
        await replacement.Runtime.ResumePendingTasksAsync(TestCancellation);

        Assert.Equal("ada", await replacement.GetResultAsync<string>(WorkflowId));
        Assert.Equal(["ada", "ada"], observed);
    });

    [Fact]
    public Task SignalRecordIsDeletedWithTheExecutionTree() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var caller = GrainId.Create("caller", "one");
        harness.Runtime.ScheduleFromInbox(WorkflowId, Harness.CreateRequest(async () => await Approved.WaitAsync(), caller));
        await harness.Manager.WriteStateAsync(TestCancellation);
        await harness.WaitForSignalWaitsAsync(1);

        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        Assert.Equal("ada", await harness.GetResultAsync<string>(WorkflowId));
        var completion = Assert.Single(harness.Transport.Completions);
        Assert.True(harness.Storage.Contains(RecordId));

        ((IOutboxMessageSource)harness.Runtime).OnRetired(completion.MessageId, OutboxMessageRetirement.Delivered);

        Assert.Empty(harness.Storage.Ids);
        await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation).AsTask());
    });

    [Fact]
    public Task DeletingTheTreeEndsAWaitWhichNeverReceivedItsSignal() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var caller = GrainId.Create("caller", "one");
        harness.Runtime.ScheduleFromInbox(WorkflowId, Harness.CreateRequest(async () =>
        {
            await DurableTask.WhenAny([Approved.WaitAsync(), DurableTask.FromResult("now")]);
            return "done";
        }, caller));
        await harness.Manager.WriteStateAsync(TestCancellation);
        Assert.Equal("done", await harness.GetResultAsync<string>(WorkflowId));
        await harness.WaitForSignalWaitsAsync(1);

        ((IOutboxMessageSource)harness.Runtime).OnRetired(Assert.Single(harness.Transport.Completions).MessageId, OutboxMessageRetirement.Delivered);
        await harness.Manager.WriteStateAsync(TestCancellation);

        await harness.WaitForSignalWaitsAsync(0);
        Assert.Empty(harness.Storage.Ids);
    });

    [Fact]
    public Task InboxAcceptsSignalsIntoTaskStateAndRejectsInvalidOnes() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var sender = GrainId.Create("sender", "one");
        var handler = new DurableTaskMessageHandler(harness.Runtime, MessagingTestHelpers.MessageTypes);
        await harness.ScheduleAsync(WorkflowId, async () => await Approved.WaitAsync());
        await harness.WaitForSignalWaitsAsync(1);

        Assert.True(handler.TryAccept(SignalMessage(sender, WorkflowId, "ada"), out _));
        Assert.Equal(DurableTaskStatus.Pending, await harness.PollAsync(WorkflowId));
        await harness.Manager.WriteStateAsync(TestCancellation);
        Assert.Equal("ada", await harness.GetResultAsync<string>(WorkflowId));

        // A duplicate is accepted; the sink dead-letters a conflicting value and a signal for a task which isn't invoked
        // with the rejection as the reason.
        Assert.True(handler.TryAccept(SignalMessage(sender, WorkflowId, "ada"), out var duplicate));
        Assert.Equal(DeliveryStatus.Accepted, duplicate.Status);
        Assert.True(handler.TryAccept(SignalMessage(sender, WorkflowId, "grace"), out var conflict));
        Assert.Equal(DeliveryStatus.DeadLettered, conflict.Status);
        Assert.False(string.IsNullOrEmpty(conflict.Message));
        Assert.True(handler.TryAccept(SignalMessage(sender, RecordId, "ada"), out var notInvoked));
        Assert.Equal(DeliveryStatus.DeadLettered, notInvoked.Status);
        Assert.Contains(RecordId.ToString(), notInvoked.Message, StringComparison.Ordinal);
        Assert.Equal("ada", harness.Storage.Get(RecordId).Result!.GetResult<string>());
        Assert.Equal([WorkflowId, RecordId], harness.Storage.Ids);

        InboxMessage SignalMessage(GrainId from, TaskId taskId, string value) =>
            CreateSignalMessage(harness, from, taskId, value);
    });

    [Fact]
    public Task InvocationObservesASignalWhichTheInboxAcceptedBeforeIt() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var caller = GrainId.Create("caller", "one");
        var handler = new DurableTaskMessageHandler(harness.Runtime, MessagingTestHelpers.MessageTypes);

        // The caller committed the invocation and the signal together, and the signal is delivered first.
        Assert.True(handler.TryAccept(CreateSignalMessage(harness, caller, WorkflowId, "ada"), out var accepted));
        Assert.Equal(DeliveryStatus.Accepted, accepted.Status);
        await harness.Manager.WriteStateAsync(TestCancellation);
        Assert.Equal([RecordId], harness.Storage.Ids);

        harness.Runtime.ScheduleFromInbox(WorkflowId, Harness.CreateRequest(async () => await Approved.WaitAsync(), caller));
        await harness.Manager.WriteStateAsync(TestCancellation);

        Assert.Equal("ada", await harness.GetResultAsync<string>(WorkflowId));
        Assert.Equal([WorkflowId, RecordId], harness.Storage.Ids);

        // The record belongs to the invoked task's tree once the invocation arrives, so it is deleted with that tree.
        ((IOutboxMessageSource)harness.Runtime).OnRetired(Assert.Single(harness.Transport.Completions).MessageId, OutboxMessageRetirement.Delivered);
        Assert.Empty(harness.Storage.Ids);
    });

    [Fact]
    public Task SignalWhoseInvocationNeverArrivesIsDeletedAfterTheSendersRetryTimeout() => ActivationScheduler.RunAsync(async () =>
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var harness = new Harness(time);
        var handler = new DurableTaskMessageHandler(harness.Runtime, MessagingTestHelpers.MessageTypes);
        var retryAge = new DurableMessagingOptions().OutboxRetryTimeout;
        Assert.True(handler.TryAccept(CreateSignalMessage(harness, GrainId.Create("caller", "one"), WorkflowId, "ada"), out _));
        await harness.Manager.WriteStateAsync(TestCancellation);

        time.Advance(retryAge - TimeSpan.FromTicks(1));
        await harness.Runtime.ResumePendingTasksAsync(TestCancellation);
        Assert.Equal([RecordId], harness.Storage.Ids);

        time.Advance(TimeSpan.FromTicks(1));
        await harness.Runtime.ResumePendingTasksAsync(TestCancellation);
        Assert.Empty(harness.Storage.Ids);
    });

    private static InboxMessage CreateSignalMessage(Harness harness, GrainId from, TaskId taskId, string value) =>
        MessagingTestHelpers.CreateMessage(
            MessagingTestHelpers.MessageTypes.Signal,
            from,
            harness.Runtime.GrainId,
            taskId,
            new DurableTaskSignalMessage { TaskId = taskId, Name = Approved.Name, Value = DurableTaskResponse.FromResult(value), Sender = from });

    [Fact]
    public Task DurableExecutionRecordsEachSendSoReplayDoesNotSendAgain() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var target = GrainId.Create("target", "one");
        var markerId = WorkflowId.Child("$child-1");
        var value = DurableTaskResponse.FromResult("ada");

        await harness.Runtime.SendSignalAsync(markerId, target, TaskId.CreateRoot("order"), Approved.Name, value, throwOnRejection: true, TestCancellation);
        await harness.Runtime.SendSignalAsync(markerId, target, TaskId.CreateRoot("order"), Approved.Name, value, throwOnRejection: true, TestCancellation);
        var reuse = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Runtime.SendSignalAsync(markerId, target, TaskId.CreateRoot("order"), Approved.Name, DurableTaskResponse.FromResult("grace"), throwOnRejection: true, TestCancellation).AsTask());

        Assert.Contains("another operation", reuse.Message, StringComparison.Ordinal);
        var sent = Assert.Single(harness.Transport.Signals);
        Assert.Equal((harness.Runtime.GrainId, target, TaskId.CreateRoot("order"), "approved"), (sent.Sender, sent.Target, sent.TaskId, sent.Name));
        Assert.Equal("ada", sent.Value.GetResult<string>());
        var marker = harness.Storage.Get(markerId);
        Assert.StartsWith("$signal-send:", marker.RequestFingerprint, StringComparison.Ordinal);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, marker.Result!.Status);
    });

    [Fact]
    public Task TaskManagerInADurableExecutionSignalsAnotherTaskOfTheGrainOnce() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var orderId = TaskId.CreateRoot("order");
        await harness.ScheduleAsync(orderId, async () => await Approved.WaitAsync());
        var tasks = harness.Tasks;
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            await tasks.SignalAsync(orderId, Approved, "ada");
            return "sent";
        });

        Assert.Equal("sent", await harness.GetResultAsync<string>(WorkflowId));
        Assert.Equal("ada", await harness.GetResultAsync<string>(orderId));
        Assert.StartsWith("$signal-send:", harness.Storage.Get(WorkflowId.Child($"$send:{harness.Runtime.GrainId}:order:approved")).RequestFingerprint, StringComparison.Ordinal);
        Assert.Equal("ada", harness.Storage.Get(orderId.Child("$signal:approved")).Result!.GetResult<string>());
        Assert.Empty(harness.Transport.Signals);
    });

    [Fact]
    public Task RepeatedSendsOfOneSignalAreNumberedByOccurrence() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var orderId = TaskId.CreateRoot("order");
        var tasks = harness.Tasks;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;

        // The first send finds no task and the retry is accepted; each send reaches its own record, so the retry isn't
        // answered with the first send's recorded TaskNotFound.
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            var first = await tasks.TrySignalAsync(orderId, Approved, "ada");
            if (Interlocked.Increment(ref sends) == 1)
            {
                await release.Task;
            }

            var retry = await tasks.TrySignalAsync(orderId, Approved, "ada");
            return $"{first},{retry}";
        });
        await WaitUntilAsync(() => Volatile.Read(ref sends) == 1);
        await harness.ScheduleAsync(orderId, async () => await Approved.WaitAsync());
        release.SetResult();

        Assert.Equal("TaskNotFound,Accepted", await harness.GetResultAsync<string>(WorkflowId));
        var signal = $"$send:{harness.Runtime.GrainId}:order:approved";
        Assert.Equal(
            DurableSignalResult.TaskNotFound,
            harness.Storage.Get(WorkflowId.Child(signal)).Result!.GetResult<DurableSignalResult>());
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, harness.Storage.Get(WorkflowId.Child($"{signal}$2")).Result!.Status);
        Assert.Equal("ada", await harness.GetResultAsync<string>(orderId));
    });

    [Fact]
    public Task RepeatedSendReportsTheTargetsDuplicateOrConflict() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness();
        var orderId = TaskId.CreateRoot("order");
        await harness.ScheduleAsync(orderId, async () => await Approved.WaitAsync());
        var tasks = harness.Tasks;

        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            var first = await tasks.TrySignalAsync(orderId, Approved, "ada");
            var same = await tasks.TrySignalAsync(orderId, Approved, "ada");
            var other = await tasks.TrySignalAsync(orderId, Approved, "grace");
            return $"{first},{same},{other}";
        });

        Assert.Equal("Accepted,Duplicate,Conflict", await harness.GetResultAsync<string>(WorkflowId));
        Assert.Equal("ada", await harness.GetResultAsync<string>(orderId));
        var signal = $"$send:{harness.Runtime.GrainId}:order:approved";
        Assert.Equal(
            DurableSignalResult.Duplicate,
            harness.Storage.Get(WorkflowId.Child($"{signal}$2")).Result!.GetResult<DurableSignalResult>());
        Assert.Equal(
            DurableSignalResult.Conflict,
            harness.Storage.Get(WorkflowId.Child($"{signal}$3")).Result!.GetResult<DurableSignalResult>());
    });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    [Fact]
    public void TransportStagesSignalsWithAValueScopedIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("sender", "one");
        var target = GrainId.Create("target", "one");
        var first = DurableTaskMessageTransport.CreateSignalMessageId(sender, target, WorkflowId, "approved", "$signal:AA");

        transport.SendSignal(sender, target, WorkflowId, "approved", DurableTaskResponse.FromResult("ada"), "$signal:AA");

        var message = Assert.Single(outbox.Messages);
        Assert.Equal((first, target, DurableTaskMessageTypes.SignalSubject), (message.Envelope.MessageId, message.ReceiverId, message.Envelope.Subject));
        var body = MessagingTestHelpers.MessageTypes.Signal.Decode(message.Envelope);
        Assert.Equal((WorkflowId, "approved", "ada", sender), (body.TaskId, body.Name, body.Value.GetResult<string>(), body.Sender));
        Assert.Equal(first, DurableTaskMessageTransport.CreateSignalMessageId(sender, target, WorkflowId, "approved", "$signal:AA"));
        Assert.NotEqual(first, DurableTaskMessageTransport.CreateSignalMessageId(sender, target, WorkflowId, "approved", "$signal:BB"));
        Assert.NotEqual(first, DurableTaskMessageTransport.CreateSignalMessageId(sender, target, WorkflowId, "approved/2", "$signal:AA"));
    }

    /// <summary>A runtime over in-memory task state whose journal hook runs on each write.</summary>
    private sealed class Harness
    {
        private readonly TimeProvider _timeProvider;

        public Harness(TimeProvider? timeProvider = null)
            : this(timeProvider ?? TimeProvider.System, new StateManager(), storage: null)
        {
        }

        private Harness(TimeProvider timeProvider, StateManager manager, Storage? storage)
        {
            _timeProvider = timeProvider;
            Manager = manager;
            Storage = storage ?? new Storage(manager, timeProvider);
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(GrainId.Create("test", "one"));
            var accessor = Substitute.For<IGrainContextAccessor>();
            accessor.GrainContext.Returns(context);
            var shared = new DurableTaskGrainRuntimeShared(
                accessor,
                timeProvider,
                NullLogger<DurableTaskGrainRuntime>.Instance,
                Options.Create(new DurableTaskOptions { ResultRetentionPeriod = TimeSpan.FromHours(1) }),
                Options.Create(new DurableMessagingOptions()),
                MessagingTestHelpers.Serializer);
            Runtime = new DurableTaskGrainRuntime(Storage, Storage.LogicalTimes, shared, [Transport]);
            Runtime.InitializeForActivation();
            manager.RegisterStateMachine(DurableTaskGrainRuntime.StateMachineName, Runtime);
        }

        public DurableTaskGrainRuntime Runtime { get; }

        public IDurableTaskManager Tasks => Runtime;

        public Storage Storage { get; }

        public StateManager Manager { get; }

        public RecordingDurableTaskMessageTransport Transport { get; } = new();

        public int SignalWaitCount
        {
            get
            {
                var waits = (System.Collections.ICollection)typeof(DurableTaskGrainRuntime)
                    .GetField("_signalWaits", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Runtime)!;
                lock (waits)
                {
                    return waits.Count;
                }
            }
        }

        /// <summary>Creates the next activation's runtime over this activation's committed state.</summary>
        public Harness CreateReplacement() => new(_timeProvider, Manager, Storage);

        public static RuntimeTestDurableTaskRequest CreateRequest<T>(Func<DurableTask<T>> workflow, GrainId caller = default) =>
            new(workflow)
            {
                Context = new DurableTaskRequestContext
                {
                    CallerId = caller,
                    TargetId = GrainId.Create("test", "one"),
                    SupportsDurableCompletion = !caller.IsDefault,
                },
            };

        /// <summary>Schedules a root task as a client does, which retains its result for polling.</summary>
        public async Task ScheduleAsync<T>(TaskId taskId, Func<DurableTask<T>> workflow) =>
            await ((IDurableTaskServer)Runtime).ScheduleAsync(taskId, CreateRequest(workflow), TestCancellation);

        public async Task<T> GetResultAsync<T>(TaskId taskId) =>
            (await Tasks.GetTask<T>(taskId).GetResponseAsync(TestCancellation).WaitAsync(TimeSpan.FromSeconds(10), TestCancellation))
                .GetResult<T>();

        public async Task<DurableTaskStatus> PollAsync(TaskId taskId) =>
            await Tasks.GetTask(taskId).GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestCancellation);

        public async Task<Dictionary<TaskId, DurableTaskDiagnosticState>> GetDiagnosticsAsync()
        {
            var states = new Dictionary<TaskId, DurableTaskDiagnosticState>();
            await foreach (var (taskId, state) in ((IDurableTaskGrainExtension)Runtime).GetTasksAsync(TestCancellation))
            {
                states.Add(taskId, state);
            }

            return states;
        }

        /// <summary>Waits until the runtime holds <paramref name="count"/> signal records with registered waits.</summary>
        public async Task WaitForSignalWaitsAsync(int count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (SignalWaitCount != count)
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        public async Task<(GrainId GrainId, TaskId TaskId, long Generation, DateTimeOffset DueTime)> WaitForResumeAsync()
        {
            await Transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
            return Assert.Single(Transport.Resumes);
        }
    }

    private sealed class Storage(StateManager manager, TimeProvider timeProvider) : IDurableTaskGrainStorage
    {
        private readonly Dictionary<TaskId, State> _states = [];
        private readonly DurableTaskHierarchy _hierarchy = new();

        public DurableDictionary<TaskId, DateTimeOffset> LogicalTimes { get; } = [];

        public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks =>
            _states.Select(static entry => (entry.Key, (IDurableTaskState)entry.Value));

        public IReadOnlyList<TaskId> Ids => [.. _states.Keys.OrderBy(static id => id.ToString(), StringComparer.Ordinal)];

        public State Get(TaskId id) => _states[id];

        public bool Contains(TaskId id) => _states.ContainsKey(id);

        public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task) =>
            _hierarchy.GetNearestStoredDescendants(task, _states.ContainsKey).Select(id => (id, (IDurableTaskState)_states[id]));

        public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
        {
            if (!_states.TryGetValue(taskId, out var state))
            {
                _states.Add(taskId, state = new State { Request = request, CreatedAt = timeProvider.GetUtcNow() });
                _hierarchy.Add(taskId);
            }

            return state;
        }

        public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request) => ((State)state).Request = request;

        public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint) =>
            ((State)state).RequestFingerprint = fingerprint;

        public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint) =>
            (((State)state).RemoteTarget, ((State)state).RemoteRequestFingerprint) = (target, fingerprint);

        public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId) => ((State)state).CallerId = callerId;

        public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
        {
            var value = (State)state;
            (value.Result, value.CompletedAt, value.DueTime) = (response, timeProvider.GetUtcNow(), null);
            if (value.ResumeGeneration > 0)
            {
                value.ResumeGeneration++;
            }
        }

        public void RequestCancellation(TaskId taskId, IDurableTaskState state, string? reason = null)
        {
            if (((State)state).CancellationRequestedAt is null)
            {
                ((State)state).CancellationRequestedAt = timeProvider.GetUtcNow();
                ((State)state).CancellationReason = reason;
            }
        }

        public void Shield(TaskId taskId, IDurableTaskState state) => ((State)state).Shielded = true;

        public void SetAccepted(TaskId taskId, IDurableTaskState state, bool accepted) => ((State)state).Accepted = accepted;

        public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation) =>
            (((State)state).DueTime, ((State)state).ResumeGeneration) = (dueTime, generation);

        public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination) =>
            ((State)state).CompletionDestinations.Add(destination);

        public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination) =>
            ((State)state).CompletionDestinations.Remove(destination);

        public void RetainResult(TaskId taskId, IDurableTaskState state) => ((State)state).RetainedSince = timeProvider.GetUtcNow();

        public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
        {
            var found = _states.TryGetValue(taskId, out var value);
            state = value;
            return found;
        }

        public bool RemoveTask(TaskId taskId)
        {
            if (!_states.Remove(taskId))
            {
                return false;
            }

            _hierarchy.Remove(taskId, _states.ContainsKey);
            return true;
        }

        public void Clear()
        {
            _states.Clear();
            _hierarchy.Clear();
        }

        public ValueTask WriteAsync(CancellationToken cancellationToken) => manager.WriteStateAsync(cancellationToken);

        public ValueTask ReadAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class State : IDurableTaskState
    {
        public DurableTaskResponse? Result { get; set; }
        public HashSet<DurableTaskSubscriber> CompletionDestinations { get; } = [];
        public IDurableTaskRequest? Request { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? CancellationRequestedAt { get; set; }
        public string? CancellationReason { get; set; }
        public bool Shielded { get; set; }
        public bool? Accepted { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? DueTime { get; set; }
        public long ResumeGeneration { get; set; }
        public string? RequestFingerprint { get; set; }
        public DateTimeOffset? RetainedSince { get; set; }
        public GrainId RemoteTarget { get; set; }
        public string? RemoteRequestFingerprint { get; set; }
        public GrainId CallerId { get; set; }

        // This storage doesn't order completions, so the runtime records each completion decision.
        public long CompletionSequence => 0;
        IReadOnlySet<DurableTaskSubscriber> IDurableTaskState.CompletionDestinations => CompletionDestinations;
    }

    private sealed class DurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    /// <summary>Runs the journal write protocol: preparation, capture, an optional storage delay, and completion.</summary>
    private sealed class StateManager : IJournaledStateManager
    {
        private readonly Dictionary<string, IStateMachine> _stateMachines = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public Func<ValueTask>? AfterWriteStarted { get; set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IStateMachine stateMachine) => _stateMachines[name] = stateMachine;

        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? stateMachine) =>
            _stateMachines.TryGetValue(name, out stateMachine);

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                var stateMachines = _stateMachines.Values.ToArray();
                foreach (var stateMachine in stateMachines)
                {
                    await stateMachine.PrepareWriteAsync(cancellationToken);
                }

                foreach (var stateMachine in stateMachines)
                {
                    stateMachine.WritePendingEntries(default);
                }

                if (AfterWriteStarted is { } afterWriteStarted)
                {
                    await afterWriteStarted();
                }

                foreach (var stateMachine in stateMachines)
                {
                    stateMachine.OnWriteCompleted();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }
}
