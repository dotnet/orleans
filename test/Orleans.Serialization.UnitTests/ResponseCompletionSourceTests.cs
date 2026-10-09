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
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public class ResponseCompletionSourceTests
{
    [Fact]
    public async Task TypedCompletionRunsContinuationsAsynchronously()
    {
        var source = ResponseCompletionSourcePool.Get<int>();
        var awaiter = source.AsValueTask().GetAwaiter();
        var continuation = RegisterContinuation(awaiter);

        var response = Response.FromResult(42);
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

    [Fact]
    public async Task TypedCompletionConsumesEnvelopeAndPreservesPayload()
    {
        var payload = new[] { 17, 25, 42 };
        var response = new TrackedResponse(payload);
        var source = ResponseCompletionSourcePool.Get<int[]>();

        source.Complete(response);

        Assert.Equal(1, response.DisposeCount);
        Assert.Null(response.Result);
        Assert.Null(response.Binding);
        Assert.Same(payload, await source.AsValueTask());
        Assert.Equal(new[] { 17, 25, 42 }, payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCompletionConsumesBothResponseOverloads(bool typedOverload)
    {
        var payload = new[] { 17, 25, 42 };
        var response = ResponsePool.Get<int[]>();
        response.TypedResult = payload;
        var source = ResponseCompletionSourcePool.Get<int[]>();

        if (typedOverload) source.Complete(response);
        else source.Complete((Response)response);

        Assert.Null(response.TypedResult);
        Assert.Same(payload, await source.AsValueTask());
    }

    [Fact]
    public async Task TypedCompletionInvalidCastReleasesEnvelope()
    {
        var response = new TrackedResponse("wrong result type");
        var source = ResponseCompletionSourcePool.Get<int>();

        source.Complete(response);

        Assert.Equal(1, response.DisposeCount);
        Assert.Null(response.Result);
        Assert.Null(response.Binding);
        var exception = await Assert.ThrowsAsync<InvalidCastException>(() => source.AsValueTask().AsTask());
        Assert.Contains(typeof(int).ToString(), exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCompletionPreservesDefaultResults(bool completed)
    {
        var source = ResponseCompletionSourcePool.Get<int>();
        source.Complete(completed ? Response.Completed : new TrackedResponse(null));
        Assert.Equal(0, await source.AsValueTask());
    }

    [Fact]
    public void FailedResultExtractionReleasesEnvelope()
    {
        var failure = new InvalidOperationException("result extraction failed");
        var response = new TrackedResponse(new[] { 17, 25, 42 }) { Failure = failure };
        var source = new ResponseCompletionSource<int[]>();

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => source.Complete(response)));

        Assert.Equal(1, response.DisposeCount);
        Assert.Null(response.Binding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionCompletionConsumesEnvelopeAndPreservesException(bool typed)
    {
        var failure = new InvalidOperationException("remote failure");
        var response = new TrackedResponse(null) { Exception = failure };
        Task completion;
        if (typed)
        {
            var source = ResponseCompletionSourcePool.Get<int>();
            source.Complete(response);
            completion = source.AsValueTask().AsTask();
        }
        else
        {
            var source = ResponseCompletionSourcePool.Get();
            source.Complete(response);
            completion = source.AsValueTask().AsTask();
        }

        Assert.Equal(1, response.DisposeCount);
        Assert.Null(response.Binding);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => completion));
    }

    [Fact]
    public async Task UntypedCompletionTransfersEnvelopeToConsumer()
    {
        var payload = new[] { 17, 25, 42 };
        var response = new TrackedResponse(payload);
        var source = ResponseCompletionSourcePool.Get();

        source.Complete(response);
        var received = await source.AsValueTask();

        Assert.Same(response, received);
        Assert.Equal(0, response.DisposeCount);
        Assert.Same(payload, received.GetResult<int[]>());
        received.Dispose();
        Assert.Equal(1, response.DisposeCount);
        Assert.Null(response.Result);
        Assert.Null(response.Binding);
    }

    [Fact]
    public async Task UntypedVoidConsumptionReleasesEnvelope()
    {
        var response = new TrackedResponse(new[] { 17, 25, 42 });
        var source = ResponseCompletionSourcePool.Get();
        var completion = source.AsVoidValueTask();

        source.Complete(response);
        Assert.Equal(0, response.DisposeCount);
        await completion;

        Assert.Equal(1, response.DisposeCount);
        Assert.Null(response.Result);
        Assert.Null(response.Binding);
    }

    private sealed class TrackedResponse(object? payload) : Response
    {
        private object? _result = payload;
        public int DisposeCount { get; private set; }
        public object? Binding { get; private set; } = new();
        public Exception? Failure { get; init; }
        public override object? Result { get => Failure is { } failure ? throw failure : _result; set => _result = value; }
        public override Exception? Exception { get; set; }
        public override T GetResult<T>() => (T)Result!;
        public override void Dispose()
        {
            DisposeCount++;
            Result = null;
            Binding = null;
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
