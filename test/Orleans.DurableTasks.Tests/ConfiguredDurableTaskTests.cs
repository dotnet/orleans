#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Runtime.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Focused tests for <c>ConfiguredDurableTask</c>/<c>ConfiguredDurableTask&lt;TResult&gt;</c> and their shared
/// internal <c>ConfiguredDurableTaskCore&lt;TDurableTask&gt;</c> engine (in DurableTask.cs): <c>ScheduleAsync</c>,
/// the internal configured-task <c>RunAsync</c> (reached via <c>await</c>), <c>TrySetTaskId</c>, and
/// <c>GetHandleOrThrow</c> (reached via <c>CancelAsync</c>/<c>PollAsync</c>).
///
/// Two ambient-context scenarios are exercised deterministically:
///  - No ambient <see cref="DurableExecutionContext"/>: the underlying task is not <see cref="ISchedulableTask"/>,
///    so every entry point throws the documented "non-schedulable" exception. This exercises the "no parent
///    context, not schedulable" branch without touching any ambient/static state.
///  - An ambient <see cref="GrainDurableExecutionContext"/> (a real, already-covered production type) backed by
///    an NSubstitute <c>IDurableTaskGrainRuntime</c>: <c>DurableExecutionContext.SetCurrentContext</c> is set
///    immediately before constructing the <c>ConfiguredDurableTask</c> (so it is captured as the task's
///    <c>ParentContext</c> snapshot) and reset in a <c>finally</c> block immediately after, so no ambient state
///    leaks across tests or across the await boundary.
/// </summary>
[TestCategory("BVT")]
public class ConfiguredDurableTaskTests
{
    private static ConfiguredDurableTask CreateWithAmbientParent(DurableExecutionContext context, DurableTask task)
    {
        DurableExecutionContext.SetCurrentContext(context);
        try
        {
            return new ConfiguredDurableTask(task);
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(null);
        }
    }

    private static ConfiguredDurableTask<TResult> CreateWithAmbientParent<TResult>(DurableExecutionContext context, DurableTask<TResult> task)
    {
        DurableExecutionContext.SetCurrentContext(context);
        try
        {
            return new ConfiguredDurableTask<TResult>(task);
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(null);
        }
    }

    [Fact]
    public async Task ScheduleAsync_WithNoAmbientContextAndNonSchedulableTask_ThrowsNonSchedulableException()
    {
        var configured = new ConfiguredDurableTask(DurableTask.Run(static _ => { }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await configured.ScheduleAsync(TestContext.Current.CancellationToken));
        Assert.Contains("does not support scheduling", exception.Message);

        // A second call on the same instance must also throw (TrySetTaskId's "already set" guard means the
        // random id assigned by the first call is reused rather than re-derived, but the outcome is identical
        // either way since the underlying task is still not schedulable).
        var secondException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await configured.CancelAsync(CancellationToken.None));
        Assert.Contains("does not support scheduling", secondException.Message);
    }

    [Fact]
    public async Task WithId_CalledTwice_OnlyTheFirstNameIsHonored()
    {
        var configured = new ConfiguredDurableTask(DurableTask.Run(static _ => { }))
            .WithId("first-name");
        Assert.Equal("first-name", configured.TaskId.ToString());

        // TrySetTaskId's "already set" guard means the second WithId call is silently ignored.
        configured = configured.WithId("second-name");
        Assert.Equal("first-name", configured.TaskId.ToString());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await configured.ScheduleAsync(TestContext.Current.CancellationToken));
        Assert.Contains("does not support scheduling", exception.Message);
    }

    [Fact]
    public async Task PollAsync_WithNoAmbientContextAndNonSchedulableTask_ThrowsViaGetHandleOrThrow()
    {
        var configured = new ConfiguredDurableTask(DurableTask.Run(static _ => { }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await configured.PollAsync(new PollingOptions(), CancellationToken.None));
        Assert.Contains("does not support scheduling", exception.Message);
    }

    [Fact]
    public async Task ScheduleAsync_WithAmbientParentContext_CreatesChildTaskIdAndDelegatesToParentContext()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var ownTaskId = TaskId.Create("root");
        var context = new GrainDurableExecutionContext(ownTaskId, runtime);
        var expectedHandle = Substitute.For<IScheduledTaskHandle>();
        var capturedTaskId = default(TaskId);
        runtime.ScheduleChildAsync(Arg.Do<TaskId>(t => capturedTaskId = t), Arg.Any<DurableTask>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IScheduledTaskHandle>(expectedHandle));

        var configured = CreateWithAmbientParent(context, DurableTask.Run(static _ => { }));

        var scheduled = await configured.ScheduleAsync(TestContext.Current.CancellationToken);

        var expectedChildTaskId = ownTaskId.Child("unnamed:0");
        Assert.Equal(expectedChildTaskId, capturedTaskId);
        _ = Assert.IsType<ScheduledDurableTask>(scheduled);
        await runtime.Received(1).ScheduleChildAsync(expectedChildTaskId, configured.Task, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Await_WithAmbientParentContext_SchedulesChildTaskAndReturnsResultFromHandle()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var ownTaskId = TaskId.Create("root");
        var context = new GrainDurableExecutionContext(ownTaskId, runtime);
        var handle = Substitute.For<IScheduledTaskHandle>();
        var response = DurableTaskResponse.FromResult(42);
        var capturedTaskId = default(TaskId);
        runtime.ScheduleChildAsync(Arg.Do<TaskId>(t => capturedTaskId = t), Arg.Any<DurableTask>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IScheduledTaskHandle>(handle));
        handle.WaitAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<DurableTaskResponse>(response));

        var configured = CreateWithAmbientParent(context, DurableTask.FromResult(0));

        var result = await configured;

        Assert.Equal(42, result);
        var expectedChildTaskId = ownTaskId.Child("unnamed:0");
        Assert.Equal(expectedChildTaskId, capturedTaskId);
        await runtime.Received(1).ScheduleChildAsync(expectedChildTaskId, configured.Task, Arg.Any<CancellationToken>());
        await handle.Received(1).WaitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_WithAmbientParentContext_CreatesChildTaskIdAndDelegatesToParentHandle()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var ownTaskId = TaskId.Create("root");
        var context = new GrainDurableExecutionContext(ownTaskId, runtime);
        var handle = Substitute.For<IScheduledTaskHandle>();
        var capturedTaskId = default(TaskId);
        runtime.GetScheduledTaskHandle(Arg.Do<TaskId>(t => capturedTaskId = t)).Returns(handle);

        var configured = CreateWithAmbientParent(context, DurableTask.Run(static _ => { }));

        var result = await configured.CancelAsync(CancellationToken.None);

        Assert.True(result);
        var expectedChildTaskId = ownTaskId.Child("unnamed:0");
        Assert.Equal(expectedChildTaskId, capturedTaskId);
        await handle.Received(1).CancelAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_WithAmbientParentContext_ReturnsStatusMappedFromHandleResponse()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var ownTaskId = TaskId.Create("root");
        var context = new GrainDurableExecutionContext(ownTaskId, runtime);
        var handle = Substitute.For<IScheduledTaskHandle>();
        var capturedTaskId = default(TaskId);
        runtime.GetScheduledTaskHandle(Arg.Do<TaskId>(t => capturedTaskId = t)).Returns(handle);
        var pollingOptions = new PollingOptions();
        handle.PollAsync(pollingOptions, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Completed));

        var configured = CreateWithAmbientParent(context, DurableTask.Run(static _ => { }));

        var status = await configured.PollAsync(pollingOptions, CancellationToken.None);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, status);
        var expectedChildTaskId = ownTaskId.Child("unnamed:0");
        Assert.Equal(expectedChildTaskId, capturedTaskId);
        await handle.Received(1).PollAsync(pollingOptions, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_GenericConfiguredTask_WithAmbientParentContext_CreatesChildTaskIdAndDelegatesToParentContext()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var ownTaskId = TaskId.Create("root");
        var context = new GrainDurableExecutionContext(ownTaskId, runtime);
        var expectedHandle = Substitute.For<IScheduledTaskHandle>();
        var capturedTaskId = default(TaskId);
        runtime.ScheduleChildAsync(Arg.Do<TaskId>(t => capturedTaskId = t), Arg.Any<DurableTask>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IScheduledTaskHandle>(expectedHandle));

        var configured = CreateWithAmbientParent(context, DurableTask.FromResult(7));

        var scheduled = await configured.ScheduleAsync(TestContext.Current.CancellationToken);

        var expectedChildTaskId = ownTaskId.Child("unnamed:0");
        Assert.Equal(expectedChildTaskId, capturedTaskId);
        _ = Assert.IsType<ScheduledDurableTask<int>>(scheduled);
        await runtime.Received(1).ScheduleChildAsync(expectedChildTaskId, configured.Task, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StructCopies_NonGenericStandalone_SchedulePollAndCancelUseOneStableId()
    {
        var task = new RecordingSchedulableTask();
        var configured = new ConfiguredDurableTask(task);
        var copy = configured;

        var scheduled = await configured.ScheduleAsync(TestContext.Current.CancellationToken);
        _ = await copy.PollAsync(new PollingOptions(), CancellationToken.None);
        _ = await configured.CancelAsync(CancellationToken.None);

        Assert.Equal(scheduled.Id, configured.TaskId);
        Assert.Equal(configured.TaskId, copy.TaskId);
        Assert.All(task.ObservedIds, id => Assert.Equal(configured.TaskId, id));
    }

    [Fact]
    public async Task StructCopies_GenericStandalone_SchedulePollAndCancelUseOneStableId()
    {
        var task = new RecordingSchedulableTask<int>();
        var configured = new ConfiguredDurableTask<int>(task);
        var copy = configured;

        var scheduled = await copy.ScheduleAsync(TestContext.Current.CancellationToken);
        _ = await configured.PollAsync(new PollingOptions(), CancellationToken.None);
        _ = await copy.CancelAsync(CancellationToken.None);

        Assert.Equal(scheduled.Id, configured.TaskId);
        Assert.Equal(configured.TaskId, copy.TaskId);
        Assert.All(task.ObservedIds, id => Assert.Equal(configured.TaskId, id));
    }

    [Fact]
    public async Task StructCopies_NonGenericParent_SchedulePollAndCancelUseOneStableChildId()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var context = new GrainDurableExecutionContext(TaskId.Create("parent"), runtime);
        var handle = new RecordingScheduledTaskHandle();
        runtime.ScheduleChildAsync(Arg.Any<TaskId>(), Arg.Any<DurableTask>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                handle.TaskId = call.Arg<TaskId>();
                return new ValueTask<IScheduledTaskHandle>(handle);
            });
        runtime.GetScheduledTaskHandle(Arg.Any<TaskId>()).Returns(handle);
        var configured = CreateWithAmbientParent(context, DurableTask.Run(static _ => { }));
        var copy = configured;

        var scheduled = await copy.ScheduleAsync(TestContext.Current.CancellationToken);
        _ = await configured.PollAsync(new PollingOptions(), CancellationToken.None);
        _ = await copy.CancelAsync(CancellationToken.None);

        Assert.Equal(scheduled.Id, configured.TaskId);
        Assert.Equal(configured.TaskId, copy.TaskId);
        runtime.Received(2).GetScheduledTaskHandle(configured.TaskId);
    }

    [Fact]
    public async Task StructCopies_GenericParent_SchedulePollAndCancelUseOneStableChildId()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var context = new GrainDurableExecutionContext(TaskId.Create("parent"), runtime);
        var handle = new RecordingScheduledTaskHandle();
        runtime.ScheduleChildAsync(Arg.Any<TaskId>(), Arg.Any<DurableTask>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                handle.TaskId = call.Arg<TaskId>();
                return new ValueTask<IScheduledTaskHandle>(handle);
            });
        runtime.GetScheduledTaskHandle(Arg.Any<TaskId>()).Returns(handle);
        var configured = CreateWithAmbientParent(context, DurableTask.FromResult(1));
        var copy = configured;

        var scheduled = await configured.ScheduleAsync(TestContext.Current.CancellationToken);
        _ = await copy.PollAsync(new PollingOptions(), CancellationToken.None);
        _ = await configured.CancelAsync(CancellationToken.None);

        Assert.Equal(scheduled.Id, configured.TaskId);
        Assert.Equal(configured.TaskId, copy.TaskId);
        runtime.Received(2).GetScheduledTaskHandle(configured.TaskId);
    }

    private sealed class RecordingSchedulableTask : DurableTask, ISchedulableTask
    {
        private readonly RecordingScheduledTaskHandle _handle = new();
        public List<TaskId> ObservedIds { get; } = [];
        public bool CommitsDurableState => false;

        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            ObservedIds.Add(taskId);
            _handle.TaskId = taskId;
            return ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.Pending);
        }

        public IScheduledTaskHandle GetHandle(TaskId taskId)
        {
            ObservedIds.Add(taskId);
            return _handle;
        }

        protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSchedulableTask<TResult> : DurableTask<TResult>, ISchedulableTask
    {
        private readonly RecordingScheduledTaskHandle _handle = new();
        public List<TaskId> ObservedIds { get; } = [];
        public bool CommitsDurableState => false;

        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            ObservedIds.Add(taskId);
            _handle.TaskId = taskId;
            return ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.Pending);
        }

        public IScheduledTaskHandle GetHandle(TaskId taskId)
        {
            ObservedIds.Add(taskId);
            return _handle;
        }

        protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingScheduledTaskHandle : IScheduledTaskHandle
    {
        public TaskId TaskId { get; set; }
        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.Pending);
        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.Pending);
    }
}
