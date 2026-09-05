using System.Distributed.DurableTasks;
using NSubstitute;
using Orleans.Runtime.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class DurableTaskMethodBuilderTests
{
    [Fact]
    public async Task VoidMethod_RestoresDurableContextAfterAsynchronousSuspension()
    {
        var context = CreateContext();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<DurableExecutionContext?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskRuntimeHelper.RunAsync(RunAsync(), context).AsTask();
        Assert.Null(DurableExecutionContext.CurrentContext);

        resume.SetResult();
        (await execution.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ThrowIfExceptionResponse();

        Assert.Same(
            context,
            await observed.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));

        async DurableTask RunAsync()
        {
            await resume.Task.ConfigureAwait(false);
            observed.SetResult(DurableExecutionContext.CurrentContext);
        }
    }

    [Fact]
    public async Task GenericMethod_RestoresDurableContextAfterAsynchronousSuspension()
    {
        var context = CreateContext();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<DurableExecutionContext?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskRuntimeHelper.RunAsync(RunAsync(), context).AsTask();
        Assert.Null(DurableExecutionContext.CurrentContext);

        resume.SetResult();
        var response = await execution.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Same(
            context,
            await observed.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));

        async DurableTask<int> RunAsync()
        {
            await resume.Task.ConfigureAwait(false);
            observed.SetResult(DurableExecutionContext.CurrentContext);
            return 42;
        }
    }

    [Fact]
    public async Task AsynchronousContinuation_UsesExecutionContextScheduler()
    {
        var scheduler = new RecordingContinuationScheduler();
        var context = new GrainDurableExecutionContext(
            TaskId.CreateRandom(),
            Substitute.For<IDurableTaskGrainRuntime>(),
            scheduler);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskRuntimeHelper.RunAsync(RunAsync(), context).AsTask();
        resume.SetResult();
        await scheduler.ContinuationScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted);

        scheduler.RunContinuation();
        (await execution.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ThrowIfExceptionResponse();

        async DurableTask RunAsync()
        {
            await resume.Task.ConfigureAwait(false);
        }
    }

    private static GrainDurableExecutionContext CreateContext()
        => new(TaskId.CreateRandom(), Substitute.For<IDurableTaskGrainRuntime>());

    private sealed class RecordingContinuationScheduler : IDurableTaskContinuationScheduler
    {
        private Action? _continuation;

        public TaskCompletionSource ContinuationScheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action WrapContinuation(Action continuation) => () =>
        {
            _continuation = continuation;
            ContinuationScheduled.TrySetResult();
        };

        public void RunContinuation() =>
            Interlocked.Exchange(ref _continuation, null)!.Invoke();
    }
}
