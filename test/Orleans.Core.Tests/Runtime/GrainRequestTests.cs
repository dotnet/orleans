using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace NonSilo.Tests.Runtime;

/// <summary>
/// Tests for the request which <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken, string?)"/> queues.
/// </summary>
[TestCategory("BVT"), TestCategory("Scheduler")]
public class GrainRequestTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Invoke_CanceledWhileEarlierCancellationCallbackRuns_DoesNotRunCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var callbackRan = false;
        var request = new GrainRequest<object?, object?>(
            (_, _) =>
            {
                callbackRan = true;
                return Task.CompletedTask;
            },
            null,
            null,
            cancellation.Token);

        // Cancellation runs callbacks in reverse registration order, so this one runs before the request's.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var blocker = cancellation.Token.Register(() =>
        {
            entered.Set();
            release.Wait(Timeout);
        });

        var cancel = Task.Run(cancellation.Cancel, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(Timeout, TestContext.Current.CancellationToken));

        await request.Invoke();
        release.Set();
        await cancel;

        Assert.False(callbackRan);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Task);
    }

    [Fact]
    public async Task Complete_CallbackFailure_IsReportedByRequest()
    {
        var exception = new InvalidOperationException("Request failed.");
        var request = new GrainRequest<object?, object?>((_, _) => Task.FromException(exception), null, null, default);

        var response = await request.Invoke();

        Assert.True(((ILocalRequest)request).Complete(response));
        Assert.Same(exception, await Assert.ThrowsAsync<InvalidOperationException>(() => request.Task));
    }

    [Fact]
    public async Task Complete_FilterFailureAfterCallback_IsNotReportedByRequest()
    {
        var request = new GrainRequest<object?, string>((_, _) => Task.FromResult("result"), null, null, default);

        await request.Invoke();

        Assert.False(((ILocalRequest)request).Complete(Response.FromException(new InvalidOperationException("Filter failed."))));
        Assert.Equal("result", await request.Task);
    }

    [Fact]
    public async Task Complete_FilterFailureWithoutCallback_IsReportedByRequest()
    {
        var callbackRan = false;
        var request = new GrainRequest<object?, object?>(
            (_, _) =>
            {
                callbackRan = true;
                return Task.CompletedTask;
            },
            null,
            null,
            default);

        Assert.True(((ILocalRequest)request).Complete(Response.FromException(new InvalidOperationException("Filter failed."))));
        Assert.False(callbackRan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.Task);
    }

    [Fact]
    public async Task Complete_FilterFailureRacingCancellation_ReportsOnlyFailuresTheTaskCarries()
    {
        var filterException = new InvalidOperationException("Filter failed.");
        var response = Response.FromException(filterException);
        for (var i = 0; i < 2_000; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var request = new GrainRequest<object?, object?>((_, _) => Task.CompletedTask, null, null, cancellation.Token);
            using var start = new Barrier(2);
            var cancel = Task.Run(() =>
            {
                start.SignalAndWait();
                cancellation.Cancel();
            }, TestContext.Current.CancellationToken);

            start.SignalAndWait(TestContext.Current.CancellationToken);
            var reported = ((ILocalRequest)request).Complete(response);
            await cancel;

            // The request reports the filter's failure exactly when its task carries that failure.
            Assert.Equal(request.Task.IsFaulted && ReferenceEquals(request.Task.Exception!.InnerException, filterException), reported);
        }
    }

    [Fact]
    public async Task Reject_QueuedRequest_FaultsWithoutRunningCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var callbackRan = false;
        var request = new GrainRequest<object?, object?>(
            (_, _) =>
            {
                callbackRan = true;
                return Task.CompletedTask;
            },
            null,
            null,
            cancellation.Token);
        var exception = new OrleansMessageRejectionException("Rejected.");

        ((ILocalRequest)request).Reject(exception);
        cancellation.Cancel();
        await request.Invoke();

        Assert.False(callbackRan);
        Assert.Same(exception, await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => request.Task));
    }

    [Fact]
    public void Describe_WithoutMethodName_DescribesRequestAsGrainRequestInvoker()
    {
        var request = new GrainRequest<object?, object?>((_, _) => Task.CompletedTask, null, null, TestContext.Current.CancellationToken);

        Assert.Equal("IGrainRequestInvoker", request.GetInterfaceName());
        Assert.Equal("RunRequestAsync", request.GetMethodName());
        Assert.Equal("IGrainRequestInvoker/RunRequestAsync", request.GetActivityName());
        Assert.Equal(typeof(IGrainRequestInvoker), request.GetInterfaceType());
        Assert.Equal(0, request.GetArgumentCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => request.GetArgument(0));
    }

    [Fact]
    public void Describe_WithMethodName_UsesMethodName()
    {
        var request = new GrainRequest<object?, object?>((_, _) => Task.CompletedTask, null, "Drain", TestContext.Current.CancellationToken);

        Assert.Equal("IGrainRequestInvoker", request.GetInterfaceName());
        Assert.Equal("Drain", request.GetMethodName());
        Assert.Equal("IGrainRequestInvoker/Drain", request.GetActivityName());
        Assert.Equal(0, request.GetArgumentCount());
    }

    [Fact]
    public async Task CompletionSource_Complete_CompletesTask()
    {
        var request = new GrainRequest<object?, string>((_, _) => Task.FromResult("unused"), null, null, TestContext.Current.CancellationToken);

        ((IResponseCompletionSource)request).Complete(Response.FromResult("filtered"));

        Assert.Equal("filtered", await request.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LocalRequestOptions_Default_KeepsActivationAliveWithoutInterleavingOrQueueLimit()
    {
        Assert.True(default(LocalRequestOptions).KeepAlive);
        Assert.True(new LocalRequestOptions().KeepAlive);
        Assert.False(new LocalRequestOptions { KeepAlive = false }.KeepAlive);
        Assert.False(default(LocalRequestOptions).Interleave);
        Assert.False(default(LocalRequestOptions).EnforceQueueLimit);
    }
}
