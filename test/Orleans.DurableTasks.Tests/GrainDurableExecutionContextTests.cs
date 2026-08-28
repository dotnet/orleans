#nullable enable
using System;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Runtime.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class GrainDurableExecutionContextTests
{
    // Note: DurableExecutionContext declares ScheduleChildTaskAsync/ScheduleDelayAsync/GetChildTaskHandle/CreateChildTaskId
    // as `protected internal`. Because System.Distributed.DurableTasks.csproj grants InternalsVisibleTo to this test
    // assembly, those members remain callable here via a base-typed (DurableExecutionContext) reference, even though
    // GrainDurableExecutionContext's own override narrows the accessor to `protected` (required since it overrides
    // across assembly boundaries).
    private static (DurableExecutionContext Context, IDurableTaskGrainRuntime Runtime) CreateContext(TaskId? ownTaskId = null)
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var taskId = ownTaskId ?? TaskId.None;
        DurableExecutionContext context = new GrainDurableExecutionContext(taskId, runtime);
        return (context, runtime);
    }

    [Fact]
    public void CreateChildTaskId_UnnamedChildren_AreSequentiallyNumbered()
    {
        var (context, _) = CreateContext();

        var first = context.CreateChildTaskId(null);
        var second = context.CreateChildTaskId("");
        var third = context.CreateChildTaskId("   ");

        Assert.Equal("unnamed:0", first.ToString());
        Assert.Equal("unnamed:1", second.ToString());
        Assert.Equal("unnamed:2", third.ToString());
    }

    [Fact]
    public void CreateChildTaskId_NamedChildren_AreSuffixedOnCollision()
    {
        var (context, _) = CreateContext();

        var first = context.CreateChildTaskId("foo");
        var second = context.CreateChildTaskId("foo");
        var third = context.CreateChildTaskId("foo");

        Assert.Equal("named:3:foo:0", first.ToString());
        Assert.Equal("named:3:foo:1", second.ToString());
        Assert.Equal("named:3:foo:2", third.ToString());
    }

    [Fact]
    public void CreateChildTaskId_DistinctNames_TrackSeparateSequences()
    {
        var (context, _) = CreateContext();

        Assert.Equal("named:3:foo:0", context.CreateChildTaskId("foo").ToString());
        Assert.Equal("named:3:bar:0", context.CreateChildTaskId("bar").ToString());
        Assert.Equal("named:3:foo:1", context.CreateChildTaskId("foo").ToString());
        Assert.Equal("named:3:bar:1", context.CreateChildTaskId("bar").ToString());
    }

    [Fact]
    public void CreateChildTaskId_NamedUnnamedAndSuffixLikeNamesAreInjective()
    {
        var (context, _) = CreateContext();

        var unnamed = context.CreateChildTaskId(null);
        var namedZero = context.CreateChildTaskId("0");
        var firstFoo = context.CreateChildTaskId("foo");
        var namedFooSuffix = context.CreateChildTaskId("foo.1");
        var secondFoo = context.CreateChildTaskId("foo");

        Assert.Equal(5, new HashSet<TaskId>
        {
            unnamed,
            namedZero,
            firstFoo,
            namedFooSuffix,
            secondFoo,
        }.Count);
    }

    [Fact]
    public async Task ScheduleChildTaskAsync_DefaultTaskId_ThrowsArgumentOutOfRangeException()
    {
        var (context, _) = CreateContext();
        var task = DurableTask.Run(static _ => { });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await context.ScheduleChildTaskAsync(default, task, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeCancellationRegistration_ConcurrentWithCancel_DoesNotThrow()
    {
        for (var i = 0; i < 500; i++)
        {
            var (context, _) = CreateContext();
            var registration = context.RegisterCancellationCallback(static _ => Task.CompletedTask);

            await Task.WhenAll(
                Task.Run(registration.Dispose, TestContext.Current.CancellationToken),
                context.CancelAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task RegisterCancellationCallback_AfterCancellationIsRejected()
    {
        var (context, _) = CreateContext();

        await context.CancelAsync(CancellationToken.None);

        Assert.Throws<OperationCanceledException>(
            () => context.RegisterCancellationCallback(static _ => Task.CompletedTask));
    }

    [Fact]
    public async Task RegisterDeactivationCallback_AfterDeactivationIsRejected()
    {
        var (context, _) = CreateContext();

        await ((GrainDurableExecutionContext)context).DeactivateForActivationAsync(CancellationToken.None);

        Assert.Throws<OperationCanceledException>(
            () => context.RegisterDeactivationCallback(
                static (object? _, CancellationToken _) => Task.CompletedTask,
                state: null));
    }

    [Fact]
    public async Task ScheduleChildTaskAsync_NotAChildOfOwnTaskId_ThrowsInvalidOperationException()
    {
        var ownTaskId = TaskId.Create("root");
        var (context, _) = CreateContext(ownTaskId);
        var unrelatedTaskId = TaskId.Create("unrelated");
        var task = DurableTask.Run(static _ => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.ScheduleChildTaskAsync(unrelatedTaskId, task, CancellationToken.None));
        Assert.Contains(unrelatedTaskId.ToString(), exception.Message);
        Assert.Contains(ownTaskId.ToString(), exception.Message);
    }

    [Fact]
    public async Task ScheduleDelayAsync_NotAChildOfOwnTaskId_ThrowsInvalidOperationException()
    {
        var ownTaskId = TaskId.Create("root");
        var (context, _) = CreateContext(ownTaskId);
        var unrelatedTaskId = TaskId.Create("unrelated");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.ScheduleDelayAsync(unrelatedTaskId, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public void GetChildTaskHandle_NotAChildOfOwnTaskId_ThrowsInvalidOperationException()
    {
        var ownTaskId = TaskId.Create("root");
        var (context, _) = CreateContext(ownTaskId);
        var unrelatedTaskId = TaskId.Create("unrelated");

        var exception = Assert.Throws<InvalidOperationException>(() => context.GetChildTaskHandle(unrelatedTaskId));
        Assert.Contains(unrelatedTaskId.ToString(), exception.Message);
        Assert.Contains(ownTaskId.ToString(), exception.Message);
    }

    [Fact]
    public async Task ScheduleChildTaskAsync_ChildOfOwnTaskId_DelegatesToRuntimeScheduleChildAsync()
    {
        var ownTaskId = TaskId.Create("root");
        var (context, runtime) = CreateContext(ownTaskId);
        var childTaskId = ownTaskId.Child("child");
        var task = DurableTask.Run(static _ => { });
        var expectedHandle = Substitute.For<IScheduledTaskHandle>();
        using var cts = new CancellationTokenSource();

        runtime.ScheduleChildAsync(childTaskId, task, cts.Token).Returns(new ValueTask<IScheduledTaskHandle>(expectedHandle));

        var handle = await context.ScheduleChildTaskAsync(childTaskId, task, cts.Token);

        Assert.Same(expectedHandle, handle);
        await runtime.Received(1).ScheduleChildAsync(childTaskId, task, cts.Token);
    }

    [Fact]
    public async Task ScheduleDelayAsync_ChildOfOwnTaskId_DelegatesToRuntimeScheduleDelayAsync()
    {
        var ownTaskId = TaskId.Create("root");
        var (context, runtime) = CreateContext(ownTaskId);
        var delayTaskId = ownTaskId.Child("delay");
        var dueTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();

        runtime.ScheduleDelayAsync(delayTaskId, dueTime, cts.Token).Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Completed));

        var response = await context.ScheduleDelayAsync(delayTaskId, dueTime, cts.Token);

        Assert.Same(DurableTaskResponse.Completed, response);
        await runtime.Received(1).ScheduleDelayAsync(delayTaskId, dueTime, cts.Token);
    }

    [Fact]
    public void GetChildTaskHandle_ChildOfOwnTaskId_DelegatesToRuntimeGetScheduledTaskHandle()
    {
        var ownTaskId = TaskId.Create("root");
        var (context, runtime) = CreateContext(ownTaskId);
        var childTaskId = ownTaskId.Child("child");
        var expectedHandle = Substitute.For<IScheduledTaskHandle>();

        runtime.GetScheduledTaskHandle(childTaskId).Returns(expectedHandle);

        var handle = context.GetChildTaskHandle(childTaskId);

        Assert.Same(expectedHandle, handle);
        runtime.Received(1).GetScheduledTaskHandle(childTaskId);
    }
}
