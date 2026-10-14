using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Cooperative cancellation and administrative abort of durable tasks.</summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    private static readonly TimeSpan CancellationTestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public Task CanceledBodyWhichCatchesTheCancellationCompletesWithItsOwnResult() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            try
            {
                await BlockedChild(started).WithId("work");
                return "completed";
            }
            catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
            {
                return "compensated";
            }
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).CancelAsync(rootId, TestContext.Current.CancellationToken);

        Assert.Equal("compensated", (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.NotNull(storage.Get(rootId).CancellationRequestedAt);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(TaskId.Parse("root/work")).Result!.Status);
    });

    [Fact]
    public Task CancellationWhichEscapesTheBodyCancelsTheTask() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            await BlockedChild(started).WithId("work");
            return "completed";
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).CancelAsync(rootId, TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForResponseAsync(runtime, rootId)).Status);
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(rootId).Result!.Status);
    });

    [Fact]
    public Task AttachedChildScheduledAfterCancellationIsCanceledWithoutRunning() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterRuns = 0;
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            try
            {
                await BlockedChild(started).WithId("work");
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await DurableTask.Run(_ => { Interlocked.Increment(ref afterRuns); }).WithId("after");
                return "after-ran";
            }
            catch (OperationCanceledException)
            {
                return "after-canceled";
            }
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).CancelAsync(rootId, TestContext.Current.CancellationToken);

        Assert.Equal("after-canceled", (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.Equal(0, Volatile.Read(ref afterRuns));
        Assert.False(storage.Contains(TaskId.Parse("root/after")));
    });

    [Fact]
    public Task DelayOfCanceledBodyTerminalizesPromptly() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var delayId = TaskId.Parse("root/wait");
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            try
            {
                await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("wait");
                return "elapsed";
            }
            catch (OperationCanceledException)
            {
                return "stopped-waiting";
            }
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await transport.ResumeSent.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).CancelAsync(rootId, TestContext.Current.CancellationToken);

        Assert.Equal("stopped-waiting", (await WaitForResponseAsync(runtime, rootId)).GetResult<string>());
        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(delayId).Result!.Status);
        Assert.Null(storage.Get(delayId).DueTime);
    });

    [Fact]
    public Task RecoveryReplaysCanceledBodyWithItsCancellationRequested() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var attempts = 0;
        var stepRuns = 0;
        var afterRuns = 0;
        var observedCancellation = new List<bool>();
        var firstAttemptBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            var context = DurableExecutionContext.Current!;
            var step = await DurableTask.Run(_ => Interlocked.Increment(ref stepRuns)).WithId("step");
            if (Interlocked.Increment(ref attempts) == 1)
            {
                // The first activation stops while the body waits, before cancellation is requested.
                firstAttemptBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            lock (observedCancellation)
            {
                observedCancellation.Add(context.IsCancellationRequested);
            }

            try
            {
                await DurableTask.Run(_ => Interlocked.Increment(ref afterRuns)).WithId("after");
                return -1;
            }
            catch (OperationCanceledException)
            {
                return step * 10;
            }
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstAttemptBlocked.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        // The cancellation request is durable, and the body's next attempt observes it.
        storage.RequestCancellation(rootId, storage.Get(rootId));
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.OnActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, (await WaitForResponseAsync(replacement, rootId)).GetResult<int>());
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(1, Volatile.Read(ref stepRuns));
        Assert.Equal(0, Volatile.Read(ref afterRuns));
        Assert.Equal([true], observedCancellation);
    });

    [Fact]
    public Task RecoveryTerminalizesCanceledRequestWhoseBodyNeverStarted() => ActivationScheduler.RunAsync(async () =>
    {
        var (_, storage, manager, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var request = CreateRequest(1);
        var state = storage.GetOrCreateTask(rootId, request);
        storage.RequestCancellation(rootId, state);
        var (recovered, _, _, _) = CreateRuntime(storage, manager);

        await recovered.OnActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, storage.Get(rootId).Result!.Status);
        Assert.Equal(0, request.CreateTaskCallCount);
    });

    [Fact]
    public Task InvokedTaskCanceledThroughInboxSendsItsBodysOutcomeToTheCaller() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("invoked");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 3;
            }
        }));
        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

        Accept(CreateHandler(runtime), CancellationMessage(caller, runtime.GrainId, taskId));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, (await WaitForResponseAsync(runtime, taskId)).GetResult<int>());
        Assert.Equal(3, storage.Get(taskId).Result!.GetResult<int>());
        var completion = Assert.Single(transport.Completions);
        Assert.Equal(caller, completion.Target);
        Assert.Equal(3, completion.Materialize(runtime).Response.GetResult<int>());
    });

    [Fact]
    public Task CanceledParentsRemoteChildAwaitsItsCalleeOutcome() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/remote");
        var request = CreateRemoteRequest(1);
        storage.GetOrCreate(rootId);
        var handle = await runtime.ScheduleChildAsync(childId, new TestStateManager.TestRemoteDurableTask(request), TestContext.Current.CancellationToken);

        await runtime.SignalCancellationAsync(rootId, TestContext.Current.CancellationToken);

        var cancellation = Assert.Single(transport.Cancellations);
        Assert.Equal(childId, cancellation.TaskId);
        Assert.False((await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken)).IsCompleted);
        runtime.AcceptResponseFromInbox(childId, DurableTaskResponse.FromResult(4), request.Context!.TargetId);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, (await handle.WaitAsync(TestContext.Current.CancellationToken)).GetResult<int>());
    });

    [Fact]
    public Task AbortTerminalizesTreeInOneWriteAndStopsRunningAttempts() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        TaskId[] treeIds = [rootId, TaskId.Parse("root/delay"), TaskId.Parse("root/remote"), TaskId.Parse("root/work")];
        var remoteTarget = CreateRemoteRequest(2).Context!.TargetId;
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workEnded = new TaskCompletionSource<(bool TokenCanceled, bool CancellationRequested)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            _ = await DurableTask.Delay(TimeSpan.FromDays(1)).WithId("delay").ScheduleAsync(cancellationToken);
            _ = await new TestStateManager.TestRemoteDurableTask(CreateRemoteRequest(2)).WithId("remote").ScheduleAsync(cancellationToken);
            try
            {
                return await DurableTask.Run(async cancellationToken =>
                {
                    var context = DurableExecutionContext.Current!;
                    workStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        workEnded.TrySetResult((cancellationToken.IsCancellationRequested, context.IsCancellationRequested));
                    }

                    return 5;
                }).WithId("work");
            }
            catch (Exception)
            {
                return 99;
            }
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await workStarted.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        var resume = Assert.Single(transport.Resumes);
        DurableTaskResponse?[]? resultsAtWrite = null;
        var abortsAtWrite = -1;
        manager.BeforeWrite = () =>
        {
            manager.BeforeWrite = null;
            resultsAtWrite = [.. treeIds.Select(id => storage.Get(id).Result)];
            abortsAtWrite = transport.Aborts.Count;
        };

        await ((IDurableTaskManager)runtime).AbortAsync(rootId, "operator", TestContext.Current.CancellationToken);

        Assert.All(resultsAtWrite!, result => AssertAborted(result, "operator"));
        Assert.Equal(1, abortsAtWrite);
        Assert.Equal((treeIds[2], remoteTarget, "operator"), transport.Aborts.Select(static abort => (abort.TaskId, abort.Target, abort.Reason)).Single());
        AssertAborted(await WaitForResponseAsync(runtime, rootId), "operator");

        // The running attempt observes host abort, not durable cancellation, and its result is discarded.
        Assert.Equal((true, false), await workEnded.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken));
        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, treeIds[1], resume.Generation, resume.DueTime));
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        Assert.All(treeIds, id => AssertAborted(storage.Get(id).Result, "operator"));
        Assert.All(treeIds, id => Assert.Null(storage.Get(id).CancellationRequestedAt));
        Assert.Empty(transport.Cancellations);
    });

    [Fact]
    public Task AbortOfCancelingTaskWhichIgnoresCancellationFaultsItAndDiscardsItsResult() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return 1;
        }));
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).CancelAsync(rootId, TestContext.Current.CancellationToken);
        Assert.Equal("Canceling", (await GetDiagnosticsAsync(runtime, rootId)).Status);

        await ((IDurableTaskManager)runtime).GetTask<int>(rootId).AbortAsync("stuck cleanup", TestContext.Current.CancellationToken);

        var diagnostics = await GetDiagnosticsAsync(runtime, rootId);
        Assert.Equal("Faulted", diagnostics.Status);
        Assert.Equal("DurableTaskAbortedException: stuck cleanup", diagnostics.Response);
        Assert.NotNull(diagnostics.CompletedAt);
        release.SetResult();
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        AssertAborted(storage.Get(rootId).Result, "stuck cleanup");
    });

    [Fact]
    public Task AbortLeavesTerminalAndUnknownTasksUnchanged() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, CreateRequest(1), TestContext.Current.CancellationToken);
        await WaitForResponseAsync(runtime, rootId);
        var result = storage.Get(rootId).Result;
        var writes = manager.WriteCount;

        await ((IDurableTaskServer)runtime).AbortAsync(rootId, "late", TestContext.Current.CancellationToken);
        await ((IDurableTaskServer)runtime).AbortAsync(TaskId.Parse("missing"), "unknown", TestContext.Current.CancellationToken);

        Assert.Same(result, storage.Get(rootId).Result);
        Assert.False(storage.Contains(TaskId.Parse("missing")));
        Assert.Equal(writes, manager.WriteCount);
        Assert.Empty(transport.Aborts);
        await Assert.ThrowsAsync<ArgumentException>(
            () => runtime.AbortTaskAsync(rootId, " ", TestContext.Current.CancellationToken).AsTask());
    });

    [Fact]
    public Task InboxAbortFaultsRunningInvokedTaskAndCompletesItsCaller() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, transport) = CreateRuntime();
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("invoked");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        var writes = manager.WriteCount;

        Accept(CreateHandler(runtime), AbortMessage(caller, runtime.GrainId, taskId, "caller aborted"));

        // The abort and the caller's completion are staged with the inbox batch, which commits them together.
        Assert.Equal(writes, manager.WriteCount);
        var completion = Assert.Single(transport.Completions);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(caller, completion.Target);
        AssertAborted(completion.Materialize(runtime).Response, "caller aborted");
        AssertAborted(storage.Get(taskId).Result, "caller aborted");
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task InboxAbortWhichPrecedesItsInvocationCancelsTheInvocation() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, manager, _) = CreateRuntime();
        var caller = GrainId.Create("caller", "one");
        var taskId = TaskId.Parse("early");
        var request = CreateRequest(1);

        Accept(CreateHandler(runtime), AbortMessage(caller, runtime.GrainId, taskId, "caller aborted"));
        Assert.NotNull(storage.Get(taskId).CancellationRequestedAt);
        var response = runtime.ScheduleFromInbox(taskId, request);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal(0, request.CreateTaskCallCount);
    });

    [Fact]
    public void AbortMessageCarriesReasonUnderItsOwnIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");

        transport.SendCancellation(sender, target, taskId);
        transport.SendAbort(sender, target, taskId, "operator");
        transport.SendAbort(sender, target, taskId, "operator");

        var messages = outbox.Messages.ToArray();
        Assert.Equal(3, messages.Length);
        Assert.NotEqual(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.Equal(messages[1].Envelope.MessageId, messages[2].Envelope.MessageId);
        Assert.All(messages, static message => Assert.Equal(DurableTaskMessageTypes.CancellationSubject, message.Envelope.Subject));
        Assert.Null(MessagingTestHelpers.MessageTypes.Cancellation.Decode(messages[0].Envelope).AbortReason);
        var abort = MessagingTestHelpers.MessageTypes.Cancellation.Decode(messages[1].Envelope);
        Assert.Equal((taskId, sender, "operator"), (abort.TaskId, abort.Sender, abort.AbortReason));
    }

    [Fact]
    public void AbortedResponseRoundTripsThroughSerializationAndCopy()
    {
        var original = DurableTaskResponse.FromException(new DurableTaskAbortedException("operator"));
        using var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();

        var deserialized = serializer.Deserialize<DurableTaskResponse>(serializer.SerializeToArray(original));
        var copied = services.GetRequiredService<DeepCopier<DurableTaskResponse>>().Copy(original);

        AssertAborted(deserialized, "operator");
        AssertAborted(copied, "operator");
    }

    [Fact]
    public Task AbortReachesSchedulerOwnedChildrenThroughTheirHandles() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.Parse("root");
        storage.GetOrCreate(rootId);
        var abortable = new RecordingSchedulerHandle(TaskId.Parse("root/abortable"), supportsAbort: true);
        var cancelOnly = new RecordingSchedulerHandle(TaskId.Parse("root/cancel-only"), supportsAbort: false);
        foreach (var handle in new[] { abortable, cancelOnly })
        {
            _ = await runtime.ScheduleChildAsync(
                handle.TaskId,
                new TestSchedulableTask(static (_, _) => new(DurableTaskResponse.Pending), _ => handle),
                TestContext.Current.CancellationToken);
        }

        await runtime.AbortTaskAsync(rootId, "operator", TestContext.Current.CancellationToken);

        Assert.Equal("operator", await abortable.Aborted.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken));
        await cancelOnly.Canceled.WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);
        Assert.False(abortable.Canceled.IsCompleted);
        AssertAborted(storage.Get(abortable.TaskId).Result, "operator");
        AssertAborted(storage.Get(cancelOnly.TaskId).Result, "operator");
    });

    private sealed class RecordingSchedulerHandle(TaskId taskId, bool supportsAbort) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<string> _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskId TaskId { get; } = taskId;

        public Task<string> Aborted => _aborted.Task;

        public Task Canceled => _canceled.Task;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) =>
            new(DurableTaskResponse.Pending);

        public ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            _canceled.TrySetResult();
            return default;
        }

        public ValueTask AbortAsync(string reason, CancellationToken cancellationToken)
        {
            if (!supportsAbort)
            {
                throw new NotSupportedException();
            }

            _aborted.TrySetResult(reason);
            return default;
        }
    }

    private static DurableTask<int> BlockedChild(TaskCompletionSource started) => DurableTask.Run(async cancellationToken =>
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    });

    private static async Task<DurableTaskResponse> WaitForResponseAsync(DurableTaskGrainRuntime runtime, TaskId taskId) =>
        await runtime.GetScheduledTaskHandle(taskId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(CancellationTestTimeout, TestContext.Current.CancellationToken);

    private static async Task<DurableTaskDiagnosticState> GetDiagnosticsAsync(DurableTaskGrainRuntime runtime, TaskId taskId)
    {
        await foreach (var (id, state) in ((IDurableTaskGrainExtension)runtime).GetTasksAsync(TestContext.Current.CancellationToken))
        {
            if (id == taskId)
            {
                return state;
            }
        }

        throw new Xunit.Sdk.XunitException($"Task '{taskId}' has no diagnostics.");
    }

    private static void AssertAborted(DurableTaskResponse? response, string reason)
    {
        Assert.NotNull(response);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        var exception = Assert.IsType<DurableTaskAbortedException>(response.Exception);
        Assert.Equal(reason, exception.Message);
    }

    private static InboxMessage AbortMessage(GrainId sender, GrainId receiver, TaskId taskId, string reason) =>
        MessagingTestHelpers.CreateMessage(
            MessagingTestHelpers.MessageTypes.Cancellation,
            sender,
            receiver,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender, AbortReason = reason });
}
