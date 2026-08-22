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
        (await execution.WaitAsync(TimeSpan.FromSeconds(10))).ThrowIfExceptionResponse();

        Assert.Same(context, await observed.Task.WaitAsync(TimeSpan.FromSeconds(10)));

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
        var response = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(42, response.GetResult<int>());
        Assert.Same(context, await observed.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        async DurableTask<int> RunAsync()
        {
            await resume.Task.ConfigureAwait(false);
            observed.SetResult(DurableExecutionContext.CurrentContext);
            return 42;
        }
    }

    private static GrainDurableExecutionContext CreateContext()
        => new(TaskId.CreateRandom(), Substitute.For<IDurableTaskGrainRuntime>());
}
