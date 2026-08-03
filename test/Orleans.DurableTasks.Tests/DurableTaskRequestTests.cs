#nullable enable
using System.Reflection;
using System.Distributed.DurableTasks;
using NSubstitute;
using NSubstitute.Core;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class DurableTaskRequestTests
{
    [Fact]
    public async Task RunAsync_RemoteRequest_PollsUntilCompleted()
    {
        var taskId = TaskId.Create("remote-request");
        var targetId = GrainId.Create("target", "1");
        var grainFactory = Substitute.For<IGrainFactory>();
        var remote = Substitute.For<IDurableTaskGrainExtension>();
        grainFactory.GetGrain<IDurableTaskGrainExtension>(targetId).Returns(remote);
        var request = CreateRequest(grainFactory, targetId);
        remote.ScheduleAsync(taskId, request, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Pending));
        remote.SubscribeOrPollAsync(taskId, Arg.Any<SubscribeOrPollOptions>(), Arg.Any<CancellationToken>())
            .Returns(Responses(DurableTaskResponse.Pending, DurableTaskResponse.FromResult(42)));

        var response = await DurableTaskRuntimeHelper.RunAsync(request, CreateExecutionContext(taskId));

        Assert.Equal(42, response.GetResult<int>());
        await remote.Received(1).ScheduleAsync(taskId, request, Arg.Any<CancellationToken>());
        await remote.Received(2).SubscribeOrPollAsync(
            taskId,
            Arg.Is<SubscribeOrPollOptions>(options => options.PollTimeout == TimeSpan.FromSeconds(5)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_GenericLocalRuntime_WaitsForPendingResponse()
    {
        var taskId = TaskId.Create("local-generic-request");
        var targetId = GrainId.Create("target", "2");
        var grainFactory = Substitute.For<IGrainFactory>();
        var request = CreateGenericRequest(grainFactory, targetId);
        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        var handle = Substitute.For<IScheduledTaskHandle>();
        runtime.ScheduleRemoteAsync(taskId, request, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Pending));
        runtime.GetScheduledTaskHandle(taskId).Returns(handle);
        handle.WaitAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.FromResult(73)));
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GetComponent<IDurableTaskGrainRuntime>().Returns(runtime);

        RuntimeContext.SetExecutionContext(grainContext, out var previous);
        ValueTask<DurableTaskResponse> operation;
        try
        {
            operation = DurableTaskRuntimeHelper.RunAsync(request, CreateExecutionContext(taskId));
        }
        finally
        {
            RuntimeContext.ResetExecutionContext(previous);
        }

        var response = await operation;

        Assert.Equal(73, response.GetResult<int>());
        await runtime.Received(1).ScheduleRemoteAsync(taskId, request, Arg.Any<CancellationToken>());
        await handle.Received(1).WaitAsync(Arg.Any<CancellationToken>());
        grainFactory.DidNotReceive().GetGrain<IDurableTaskGrainExtension>(Arg.Any<GrainId>());
    }

    [Fact]
    public async Task ScheduleAsync_UsesLocalRuntimeWhenAvailableAndRemoteGrainOtherwise()
    {
        var taskId = TaskId.Create("schedule-request");
        var targetId = GrainId.Create("target", "3");
        var grainFactory = Substitute.For<IGrainFactory>();
        var remote = Substitute.For<IDurableTaskGrainExtension>();
        grainFactory.GetGrain<IDurableTaskGrainExtension>(targetId).Returns(remote);
        var request = CreateRequest(grainFactory, targetId);
        remote.ScheduleAsync(taskId, request, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Completed));

        var remoteResponse = await ((ISchedulableTask)request).ScheduleAsync(taskId, CancellationToken.None);

        Assert.Same(DurableTaskResponse.Completed, remoteResponse);
        await remote.Received(1).ScheduleAsync(taskId, request, Arg.Any<CancellationToken>());

        var runtime = Substitute.For<IDurableTaskGrainRuntime>();
        runtime.ScheduleRemoteAsync(taskId, request, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.FromResult(9)));
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GetComponent<IDurableTaskGrainRuntime>().Returns(runtime);
        RuntimeContext.SetExecutionContext(grainContext, out var previous);
        ValueTask<DurableTaskResponse> localOperation;
        try
        {
            localOperation = ((ISchedulableTask)request).ScheduleAsync(taskId, CancellationToken.None);
        }
        finally
        {
            RuntimeContext.ResetExecutionContext(previous);
        }

        var localResponse = await localOperation;

        Assert.Equal(9, localResponse.GetResult<int>());
        await runtime.Received(1).ScheduleRemoteAsync(taskId, request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrainScheduledTaskHandle_DelegatesAndCachesCompletedResponses()
    {
        var taskId = TaskId.Create("handle-request");
        var targetId = GrainId.Create("target", "4");
        var grainFactory = Substitute.For<IGrainFactory>();
        var remote = Substitute.For<IDurableTaskGrainExtension>();
        grainFactory.GetGrain<IDurableTaskGrainExtension>(targetId).Returns(remote);
        var request = CreateRequest(grainFactory, targetId);
        remote.ScheduleAsync(taskId, request, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Pending));
        remote.SubscribeOrPollAsync(taskId, Arg.Any<SubscribeOrPollOptions>(), Arg.Any<CancellationToken>())
            .Returns(Responses(DurableTaskResponse.Pending, DurableTaskResponse.FromResult(21)));
        var handle = Assert.IsType<GrainScheduledTaskHandle>(request.GetHandle(taskId));

        var scheduled = await handle.ScheduleAsync(CancellationToken.None);
        var firstPoll = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        var secondPoll = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.FromSeconds(3) }, CancellationToken.None);
        var cachedPoll = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.FromDays(1) }, CancellationToken.None);
        var cachedWait = await handle.WaitAsync(CancellationToken.None);
        await handle.CancelAsync(CancellationToken.None);

        Assert.Same(DurableTaskResponse.Pending, scheduled);
        Assert.Same(DurableTaskResponse.Pending, firstPoll);
        Assert.Equal(21, secondPoll.GetResult<int>());
        Assert.Same(secondPoll, cachedPoll);
        Assert.Same(secondPoll, cachedWait);
        Assert.Same(secondPoll, handle.LastResponse);
        await remote.Received(1).ScheduleAsync(taskId, request, Arg.Any<CancellationToken>());
        await remote.Received(2).SubscribeOrPollAsync(taskId, Arg.Any<SubscribeOrPollOptions>(), Arg.Any<CancellationToken>());
        await remote.Received(1).CancelAsync(taskId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrainScheduledTaskHandle_WaitAsync_PollsUntilCompleted()
    {
        var taskId = TaskId.Create("wait-request");
        var targetId = GrainId.Create("target", "5");
        var grainFactory = Substitute.For<IGrainFactory>();
        var remote = Substitute.For<IDurableTaskGrainExtension>();
        grainFactory.GetGrain<IDurableTaskGrainExtension>(targetId).Returns(remote);
        var request = CreateRequest(grainFactory, targetId);
        remote.SubscribeOrPollAsync(taskId, Arg.Any<SubscribeOrPollOptions>(), Arg.Any<CancellationToken>())
            .Returns(Responses(DurableTaskResponse.Pending, DurableTaskResponse.FromResult(34)));
        var handle = Assert.IsType<GrainScheduledTaskHandle>(request.GetHandle(taskId));

        var response = await handle.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.Equal(34, response.GetResult<int>());
        Assert.Same(response, handle.LastResponse);
        await remote.Received(2).SubscribeOrPollAsync(
            taskId,
            Arg.Is<SubscribeOrPollOptions>(options => options.PollTimeout == TimeSpan.FromSeconds(5)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AreRequestsEquivalent_DistinguishesInterfaceMethodCountAndArguments()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var shared = CreateShared(grainFactory);
        var baseline = new TestRequest(shared, "ITest", "Run", ["a", 1]);

        Assert.True(IDurableTaskRequest.AreRequestsEquivalent(
            baseline,
            new TestRequest(shared, "ITest", "Run", ["a", 1])));
        Assert.False(IDurableTaskRequest.AreRequestsEquivalent(
            baseline,
            new TestRequest(shared, "IOther", "Run", ["a", 1])));
        Assert.False(IDurableTaskRequest.AreRequestsEquivalent(
            baseline,
            new TestRequest(shared, "ITest", "Other", ["a", 1])));
        Assert.False(IDurableTaskRequest.AreRequestsEquivalent(
            baseline,
            new TestRequest(shared, "ITest", "Run", ["a"])));
        Assert.False(IDurableTaskRequest.AreRequestsEquivalent(
            baseline,
            new TestRequest(shared, "ITest", "Run", ["a", 2])));
    }

    private static TestRequest CreateRequest(IGrainFactory grainFactory, GrainId targetId)
    {
        var result = new TestRequest(CreateShared(grainFactory));
        SetContext(result, new DurableTaskRequestContext { TargetId = targetId });
        return result;
    }

    private static TestRequest<int> CreateGenericRequest(IGrainFactory grainFactory, GrainId targetId)
    {
        var result = new TestRequest<int>(CreateShared(grainFactory));
        SetContext(result, new DurableTaskRequestContext { TargetId = targetId });
        return result;
    }

    private static DurableTaskRequestShared CreateShared(IGrainFactory grainFactory)
        => new(Substitute.For<IGrainContextAccessor>(), grainFactory);

    private static GrainDurableExecutionContext CreateExecutionContext(TaskId taskId)
        => new(taskId, Substitute.For<IDurableTaskGrainRuntime>());

    private static void SetContext(DurableTaskRequest request, DurableTaskRequestContext context)
        => typeof(DurableTaskRequest).GetProperty(nameof(DurableTaskRequest.Context))!.SetValue(request, context);

    private static void SetContext<TResult>(DurableTaskRequest<TResult> request, DurableTaskRequestContext context)
        => typeof(DurableTaskRequest<TResult>).GetProperty(nameof(DurableTaskRequest<TResult>.Context))!.SetValue(request, context);

    private static Func<CallInfo, ValueTask<DurableTaskResponse>> Responses(params DurableTaskResponse[] responses)
    {
        var queue = new Queue<DurableTaskResponse>(responses);
        return _ => new ValueTask<DurableTaskResponse>(queue.Dequeue());
    }

    private sealed class TestRequest(
        DurableTaskRequestShared shared,
        string interfaceName = "ITest",
        string methodName = "Run",
        object?[]? arguments = null) : DurableTaskRequest(shared)
    {
        private readonly object?[] _arguments = arguments ?? [];

        public override int GetArgumentCount() => _arguments.Length;
        public override object GetArgument(int index) => _arguments[index]!;
        public override void SetArgument(int index, object value) => _arguments[index] = value;
        public override object GetTarget() => this;
        public override void SetTarget(ITargetHolder holder) { }
        public override void Dispose() { }
        public override string GetMethodName() => methodName;
        public override string GetInterfaceName() => interfaceName;
        public override string GetActivityName() => $"{interfaceName}/{methodName}";
        public override Type GetInterfaceType() => typeof(TestRequest);
        public override MethodInfo GetMethod() => typeof(TestRequest).GetMethod(nameof(GetMethod))!;
        protected override DurableTask InvokeInner() => DurableTask.Run(static _ => { });
    }

    private sealed class TestRequest<TResult>(DurableTaskRequestShared shared) : DurableTaskRequest<TResult>(shared)
    {
        public override object GetTarget() => this;
        public override void SetTarget(ITargetHolder holder) { }
        public override void Dispose() { }
        public override string GetMethodName() => "Run";
        public override string GetInterfaceName() => "ITest";
        public override string GetActivityName() => "ITest/Run";
        public override Type GetInterfaceType() => typeof(TestRequest<TResult>);
        public override MethodInfo GetMethod() => typeof(TestRequest<TResult>).GetMethod(nameof(GetMethod))!;
        protected override DurableTask<TResult> InvokeInner() => DurableTask.FromResult(default(TResult)!);
    }
}
