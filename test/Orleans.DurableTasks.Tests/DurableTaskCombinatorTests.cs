#nullable enable
using System.Distributed.DurableTasks;
using System.Runtime.CompilerServices;
using NSubstitute;
using NSubstitute.Core;
using Orleans.Runtime.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class DurableTaskCombinatorTests
{
    [Fact]
    public async Task WhenAll_ReturnsEveryScheduledChild()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var handles = CreateHandles(
            ("0", DurableTaskResponse.Completed),
            ("1", DurableTaskResponse.Completed));
        ConfigureRuntime(runtime, handles);
        var context = new GrainDurableExecutionContext(TaskId.Create("root"), runtime);
        var combinator = DurableTask.WhenAll(
            [DurableTask.Run(static _ => { }), DurableTask.Run(static _ => { })]);

        var response = await RunAsync(combinator, context);
        var scheduled = response.GetResult<List<ScheduledTask>>();

        Assert.Equal(2, scheduled.Count);
        Assert.Equal(TaskId.Create("root/0"), scheduled[0].Id);
        Assert.Equal(TaskId.Create("root/1"), scheduled[1].Id);
        await runtime.Received(2).ScheduleChildAsync(
            Arg.Any<TaskId>(),
            Arg.Any<DurableTask>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenericWhenAll_ReturnsEveryScheduledChild()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var handles = CreateHandles(
            ("0", DurableTaskResponse.FromResult(1)),
            ("1", DurableTaskResponse.FromResult(2)));
        ConfigureRuntime(runtime, handles);
        var context = new GrainDurableExecutionContext(TaskId.Create("root"), runtime);
        var combinator = DurableTask.WhenAll(
            [DurableTask.FromResult(1), DurableTask.FromResult(2)]);

        var response = await RunAsync(combinator, context);
        var scheduled = response.GetResult<List<ScheduledTask<int>>>();

        Assert.Equal(2, scheduled.Count);
        Assert.Equal(1, await scheduled[0]);
        Assert.Equal(2, await scheduled[1]);
    }

    [Fact]
    public async Task WhenAny_ReturnsTheFirstCompletedChild()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var firstCompletion = new TaskCompletionSource<DurableTaskResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handles = new Dictionary<string, IScheduledTaskHandle>
        {
            ["0"] = CreateHandle("root/0", _ => new ValueTask<DurableTaskResponse>(firstCompletion.Task)),
            ["1"] = CreateHandle("root/1", _ => new ValueTask<DurableTaskResponse>(DurableTaskResponse.Completed)),
        };
        ConfigureRuntime(runtime, handles);
        var context = new GrainDurableExecutionContext(TaskId.Create("root"), runtime);
        var combinator = DurableTask.WhenAny(
            [DurableTask.Run(static _ => { }), DurableTask.Run(static _ => { })]);

        var response = await RunAsync(combinator, context);
        var completed = response.GetResult<ScheduledTask>();

        Assert.Equal(TaskId.Create("root/1"), completed.Id);
        Assert.False(firstCompletion.Task.IsCompleted);
    }

    [Fact]
    public async Task GenericWhenAny_ReturnsTheFirstCompletedChild()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var firstCompletion = new TaskCompletionSource<DurableTaskResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handles = new Dictionary<string, IScheduledTaskHandle>
        {
            ["0"] = CreateHandle("root/0", _ => new ValueTask<DurableTaskResponse>(firstCompletion.Task)),
            ["1"] = CreateHandle("root/1", _ => new ValueTask<DurableTaskResponse>(DurableTaskResponse.FromResult(2))),
        };
        ConfigureRuntime(runtime, handles);
        var context = new GrainDurableExecutionContext(TaskId.Create("root"), runtime);
        var combinator = DurableTask.WhenAny(
            [DurableTask.FromResult(1), DurableTask.FromResult(2)]);

        var response = await RunAsync(combinator, context);
        var completed = response.GetResult<ScheduledTask<int>>();

        Assert.Equal(TaskId.Create("root/1"), completed.Id);
        Assert.Equal(2, await completed);
        Assert.False(firstCompletion.Task.IsCompleted);
    }

    [Fact]
    public async Task GenericMethodException_ReturnsDurableResponse()
    {
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var context = new GrainDurableExecutionContext(TaskId.Create("root"), runtime);

        var response = await RunAsync(ThrowingGenericMethod(), context);

        var exception = Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Equal("generic failure", exception.Message);
    }

    [Fact]
    public void MethodBuilders_AcceptCompilerStateMachineRegistration()
    {
        var stateMachine = new TestStateMachine();

        DurableTaskMethodBuilder.Create().SetStateMachine(stateMachine);
        DurableTaskMethodBuilder<int>.Create().SetStateMachine(stateMachine);
    }

    private static async DurableTask<int> ThrowingGenericMethod()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("generic failure");
    }

    private sealed class TestStateMachine : IAsyncStateMachine
    {
        public void MoveNext()
        {
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
        }
    }

    private static Dictionary<string, IScheduledTaskHandle> CreateHandles(
        params (string Name, DurableTaskResponse Response)[] responses)
        => responses.ToDictionary(
            entry => entry.Name,
            entry => CreateHandle($"root/{entry.Name}", _ => new ValueTask<DurableTaskResponse>(entry.Response)));

    private static async ValueTask<DurableTaskResponse> RunAsync(
        DurableTask task,
        DurableExecutionContext context)
    {
        try
        {
            return await DurableTaskRuntimeHelper.RunAsync(task, context);
        }
        finally
        {
            DurableTaskRuntimeHelper.SetCurrentContext(null);
        }
    }

    private static IScheduledTaskHandle CreateHandle(
        string taskId,
        Func<CancellationToken, ValueTask<DurableTaskResponse>> wait)
    {
        var handle = Substitute.For<IScheduledTaskHandle>();
        handle.TaskId.Returns(TaskId.Create(taskId));
        handle.WaitAsync(Arg.Any<CancellationToken>())
            .Returns((Func<CallInfo, ValueTask<DurableTaskResponse>>)(call => wait(call.Arg<CancellationToken>())));
        return handle;
    }

    private static void ConfigureRuntime(
        IDurableTaskGrainRuntime runtime,
        IReadOnlyDictionary<string, IScheduledTaskHandle> handles)
    {
        runtime.ScheduleChildAsync(
                Arg.Any<TaskId>(),
                Arg.Any<DurableTask>(),
                Arg.Any<CancellationToken>())
            .Returns((Func<CallInfo, ValueTask<IScheduledTaskHandle>>)(call =>
            {
                var taskId = call.Arg<TaskId>();
                return new ValueTask<IScheduledTaskHandle>(handles[taskId.ToString().Split('/')[^1]]);
            }));
    }
}
