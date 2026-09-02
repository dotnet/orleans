using Orleans.Connections.Security;
using Xunit;
using TaskToApm = Orleans.Connections.Security.DuplexPipeStream.TaskToApm;

namespace Orleans.Connections.Security.Tests;

public class TaskToApmTests
{
    [Fact]
    public void Begin_CompletedTask_InvokesCallbackSynchronouslyWithExactState()
    {
        var state = new object();
        IAsyncResult? callbackResult = null;
        var callbackCount = 0;
        var beginReturned = false;

        var result = TaskToApm.Begin(
            Task.CompletedTask,
            value =>
            {
                Assert.False(beginReturned);
                callbackResult = value;
                callbackCount++;
            },
            state);
        beginReturned = true;

        Assert.Same(result, callbackResult);
        Assert.Same(state, result.AsyncState);
        Assert.True(result.CompletedSynchronously);
        Assert.True(result.IsCompleted);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task Begin_DelayedTask_InvokesCallbackAfterCompletionWithExactState()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource<IAsyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new object();

        var result = TaskToApm.Begin(operation.Task, callback.SetResult, state);

        Assert.False(result.CompletedSynchronously);
        Assert.False(result.IsCompleted);
        Assert.False(callback.Task.IsCompleted);
        operation.SetResult();
        var callbackResult = await callback.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(result, callbackResult);
        Assert.Same(state, callbackResult.AsyncState);
        Assert.False(callbackResult.CompletedSynchronously);
        Assert.True(callbackResult.IsCompleted);
    }

    [Fact]
    public async Task AsyncWaitHandle_TracksDelayedTaskCompletion()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = TaskToApm.Begin(operation.Task, callback: null, state: null);
        using var waitHandle = result.AsyncWaitHandle;

        Assert.False(waitHandle.WaitOne(0));
        operation.SetResult();
        await operation.Task;

        Assert.True(waitHandle.WaitOne(0));
        Assert.True(result.IsCompleted);
        Assert.False(result.CompletedSynchronously);
    }

    [Fact]
    public void GetTask_ReturnsExactWrappedTask()
    {
        var task = Task.FromResult(873);
        var result = TaskToApm.Begin(task, callback: null, state: new object());

        Assert.Same(task, TaskToApm.GetTask(result));
        Assert.True(result.CompletedSynchronously);
    }

    [Fact]
    public void End_CompletedNonGenericTask_ReturnsSuccessfully()
    {
        var task = Task.CompletedTask;
        var result = TaskToApm.Begin(task, callback: null, state: null);

        TaskToApm.End(result);

        Assert.True(result.IsCompleted);
        Assert.Same(task, TaskToApm.GetTask(result));
    }

    [Fact]
    public void End_CompletedGenericTask_ReturnsExactResult()
    {
        var task = Task.FromResult("exact-result");
        var result = TaskToApm.Begin(task, callback: null, state: null);

        var value = TaskToApm.End<string>(result);

        Assert.Equal("exact-result", value);
        Assert.Same(task, TaskToApm.GetTask(result));
    }

    [Fact]
    public async Task End_DelayedNonGenericTask_WaitsForCompletion()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = TaskToApm.Begin(operation.Task, callback: null, state: null);
        var endStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endTask = Task.Run(
            () =>
            {
                endStarted.SetResult();
                TaskToApm.End(result);
            },
            TestContext.Current.CancellationToken);

        await endStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(endTask.IsCompleted);
        operation.SetResult();
        await endTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task End_DelayedGenericTask_WaitsAndReturnsExactResult()
    {
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = TaskToApm.Begin(operation.Task, callback: null, state: null);
        var endStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endTask = Task.Run(
            () =>
            {
                endStarted.SetResult();
                return TaskToApm.End<int>(result);
            },
            TestContext.Current.CancellationToken);

        await endStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(endTask.IsCompleted);
        operation.SetResult(1_237);
        Assert.Equal(1_237, await endTask.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void End_FaultedTasks_PropagateOriginalException()
    {
        var nonGenericException = new InvalidOperationException("non-generic failure");
        var genericException = new FormatException("generic failure");
        var nonGeneric = TaskToApm.Begin(
            Task.FromException(nonGenericException),
            callback: null,
            state: null);
        var generic = TaskToApm.Begin(
            Task.FromException<int>(genericException),
            callback: null,
            state: null);

        Assert.Same(nonGenericException, Assert.Throws<InvalidOperationException>(() => TaskToApm.End(nonGeneric)));
        Assert.Same(genericException, Assert.Throws<FormatException>(() => TaskToApm.End<int>(generic)));
        Assert.True(nonGeneric.IsCompleted);
        Assert.True(generic.IsCompleted);
    }

    [Fact]
    public void End_CanceledTasks_PropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var nonGeneric = TaskToApm.Begin(
            Task.FromCanceled(cancellation.Token),
            callback: null,
            state: null);
        var generic = TaskToApm.Begin(
            Task.FromCanceled<int>(cancellation.Token),
            callback: null,
            state: null);

        var nonGenericException = Assert.Throws<TaskCanceledException>(() => TaskToApm.End(nonGeneric));
        var genericException = Assert.Throws<TaskCanceledException>(() => TaskToApm.End<int>(generic));

        Assert.Equal(cancellation.Token, nonGenericException.CancellationToken);
        Assert.Equal(cancellation.Token, genericException.CancellationToken);
    }

    [Fact]
    public void End_NullResult_ThrowsArgumentNullException()
    {
        var nonGeneric = Assert.Throws<ArgumentNullException>(() => TaskToApm.End(null!));
        var generic = Assert.Throws<ArgumentNullException>(() => TaskToApm.End<int>(null!));

        Assert.Equal("asyncResult", nonGeneric.ParamName);
        Assert.Equal("asyncResult", generic.ParamName);
    }

    [Fact]
    public void End_ForeignResult_ThrowsArgumentException()
    {
        IAsyncResult foreign = Task.CompletedTask;

        var nonGeneric = Assert.Throws<ArgumentException>(() => TaskToApm.End(foreign));
        var generic = Assert.Throws<ArgumentException>(() => TaskToApm.End<int>(foreign));

        Assert.Equal("asyncResult", nonGeneric.ParamName);
        Assert.Equal("asyncResult", generic.ParamName);
    }

    [Fact]
    public void End_GenericWithWrongTaskResultType_ThrowsArgumentException()
    {
        var task = Task.FromResult("wrong type");
        var result = TaskToApm.Begin(task, callback: null, state: null);

        var exception = Assert.Throws<ArgumentException>(() => TaskToApm.End<int>(result));

        Assert.Equal("asyncResult", exception.ParamName);
        Assert.Same(task, TaskToApm.GetTask(result));
    }

    [Fact]
    public void GetTask_NullOrForeignResult_ReturnsNull()
    {
        Assert.Null(TaskToApm.GetTask(null!));
        Assert.Null(TaskToApm.GetTask(Task.CompletedTask));
    }
}
