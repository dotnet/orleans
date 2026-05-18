#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;
using Xunit;

#pragma warning disable xUnit1031 // These tests manually complete ValueTaskSource awaiters and must call GetResult.

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public class ResponseCompletionSourceTests
{
    [Fact]
    public async Task TypedCompletionRunsContinuationsAsynchronously()
    {
        var source = ResponseCompletionSourcePool.Get<int>();
        var awaiter = source.AsValueTask().GetAwaiter();
        var continuation = RegisterContinuation(awaiter);

        using var response = Response.FromResult(42);
        continuation.CompletionThreadId = Thread.CurrentThread.ManagedThreadId;
        source.Complete(response);

        Assert.NotEqual(continuation.CompletionThreadId, Volatile.Read(ref continuation.ContinuationThreadId));

        await WaitForContinuation(continuation.Task);
        Assert.Equal(42, awaiter.GetResult());
    }

    [Fact]
    public async Task UntypedCompletionRunsContinuationsAsynchronously()
    {
        var source = ResponseCompletionSourcePool.Get();
        var awaiter = source.AsValueTask().GetAwaiter();
        var continuation = RegisterContinuation(awaiter);
        var response = Response.FromResult(42);
        Response? result = null;

        try
        {
            continuation.CompletionThreadId = Thread.CurrentThread.ManagedThreadId;
            source.Complete(response);

            Assert.NotEqual(continuation.CompletionThreadId, Volatile.Read(ref continuation.ContinuationThreadId));

            await WaitForContinuation(continuation.Task);
            result = awaiter.GetResult();
            Assert.Same(response, result);
            Assert.Equal(42, result.GetResult<int>());
        }
        finally
        {
            (result ?? response).Dispose();
        }
    }

    private static ContinuationProbe RegisterContinuation<T>(ValueTaskAwaiter<T> awaiter)
    {
        var continuation = new ContinuationProbe();

        awaiter.OnCompleted(() =>
        {
            Volatile.Write(ref continuation.ContinuationThreadId, Thread.CurrentThread.ManagedThreadId);
            continuation.SetResult();
        });

        return continuation;
    }

    private static async Task WaitForContinuation(Task continuation)
    {
        var completed = await Task.WhenAny(continuation, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(continuation, completed);
        await continuation;
    }

    private sealed class ContinuationProbe
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompletionThreadId = -1;
        public int ContinuationThreadId = -1;
        public Task Task => _completion.Task;

        public void SetResult() => _completion.SetResult(true);
    }
}
