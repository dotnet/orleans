using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace NonSilo.Tests.Runtime;

/// <summary>
/// Tests for the request which <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/> queues.
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
        var request = new GrainRequest<object?, object?>((_, _) => Task.FromException(exception), null, default);

        var response = await request.Invoke();

        Assert.True(((ILocalRequest)request).Complete(response));
        Assert.Same(exception, await Assert.ThrowsAsync<InvalidOperationException>(() => request.Task));
    }

    [Fact]
    public async Task Complete_FilterFailureAfterCallback_IsNotReportedByRequest()
    {
        var request = new GrainRequest<object?, string>((_, _) => Task.FromResult("result"), null, default);

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
            var request = new GrainRequest<object?, object?>((_, _) => Task.CompletedTask, null, cancellation.Token);
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
    public async Task Complete_RejectedQueuedRequest_FaultsWithoutRunningCallback()
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
            cancellation.Token);
        var exception = new OrleansMessageRejectionException("Rejected.");

        ((ILocalRequest)request).Complete(Response.FromException(exception));
        cancellation.Cancel();
        await request.Invoke();

        Assert.False(callbackRan);
        Assert.Same(exception, await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => request.Task));
    }

    [Fact]
    public void Describe_DescribesRequestAsGrainRequestInvoker()
    {
        var request = new GrainRequest<object?, object?>((_, _) => Task.CompletedTask, null, TestContext.Current.CancellationToken);

        Assert.Equal("IGrainRequestInvoker", request.GetInterfaceName());
        Assert.Equal("RunRequestAsync", request.GetMethodName());
        Assert.Equal("IGrainRequestInvoker/RunRequestAsync", request.GetActivityName());
        Assert.Equal(typeof(IGrainRequestInvoker), request.GetInterfaceType());
        Assert.Equal(0, request.GetArgumentCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => request.GetArgument(0));
    }

    [Fact]
    public async Task RunRequestAsync_SubmissionFails_FaultsTaskAndReleasesRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var grain = new ThrowingGrain();

        var (task, state) = StartRequest(grain, cancellation.Token);

        await Assert.ThrowsAsync<NotSupportedException>(() => task);

        // The uncanceled token no longer retains the request or its state.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(state.IsAlive);
        GC.KeepAlive(cancellation);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (Task Task, WeakReference State) StartRequest(IGrainBase grain, CancellationToken cancellationToken)
        {
            var state = new object();
            return (grain.RunRequestAsync(static (_, _) => { }, state, cancellationToken), new WeakReference(state));
        }
    }

    /// <summary>
    /// A grain whose context rejects every local request.
    /// </summary>
    private sealed class ThrowingGrain : IGrainBase
    {
        public IGrainContext GrainContext { get; } = DispatchProxy.Create<IGrainContext, ThrowingContext>();
    }

    public class ThrowingContext : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    [Fact]
    public async Task Complete_FilterResultWithoutCallback_CompletesTask()
    {
        var request = new GrainRequest<object?, string>((_, _) => Task.FromResult("unused"), null, TestContext.Current.CancellationToken);

        Assert.True(((ILocalRequest)request).Complete(Response.FromResult("filtered")));

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
