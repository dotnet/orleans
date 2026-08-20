using System.Collections.Concurrent;
using System.Distributed.DurableTasks;
using Xunit;

namespace System.Distributed.DurableTasks.Tests;

[Trait("Category", "BVT")]
public class DurableTaskTests
{
    [Fact]
    public async Task CompilerLoweredTaskIsDeferredAndRestoresAmbientContext()
    {
        var host = new TestHost(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var context = host.CreateContext(TaskId.CreateRoot("root"));
        var moveNextCount = 0;
        var task = Definition();

        Assert.Equal(0, moveNextCount);
        var response = await DurableTaskRuntimeHelper.RunAsync(task, context);

        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(3, moveNextCount);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask<int> Definition()
        {
            moveNextCount++;
            Assert.Same(context, DurableExecutionContext.Current);
            await Task.Yield();
            moveNextCount++;
            Assert.Same(context, DurableExecutionContext.Current);
            await Task.Delay(1).ConfigureAwait(false);
            moveNextCount++;
            Assert.Same(context, DurableExecutionContext.Current);
            return 42;
        }
    }

    [Fact]
    public async Task DelayUsesHostLogicalTime()
    {
        var now = new DateTimeOffset(2040, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var host = new TestHost(now);
        var context = host.CreateContext(TaskId.CreateRoot("delay"));

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.FromMinutes(3)), context);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(now.AddMinutes(3), host.LastDelayDueTime);
    }

    [Fact]
    public async Task DelayReceivesDurableCancellationToken()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("canceled-delay"));
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.Zero), context);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(context.CancellationToken, host.LastDelayCancellationToken);
        Assert.True(host.LastDelayCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DelegateRunnerRestoresPriorAmbientContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var outer = host.CreateContext(TaskId.CreateRoot("outer"));
        var inner = host.CreateContext(TaskId.CreateRoot("inner"));
        var task = DurableTask.Run(async _ =>
        {
            Assert.Same(inner, DurableExecutionContext.Current);
            await Task.Delay(1).ConfigureAwait(false);
            Assert.Same(inner, DurableExecutionContext.Current);
        });

        await host.RunWithAmbientAsync(outer, async () =>
        {
            var response = await DurableTaskRuntimeHelper.RunAsync(task, inner);
            if (response.Exception is { } exception)
            {
                throw exception;
            }
        });

        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task SynchronousDelegateRestoresPriorAmbientContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var outer = host.CreateContext(TaskId.CreateRoot("outer"));
        var inner = host.CreateContext(TaskId.CreateRoot("inner"));
        var task = DurableTask.Run(_ =>
        {
            Assert.Same(inner, DurableExecutionContext.Current);
            return 17;
        });

        await host.RunWithAmbientAsync(outer, async () =>
        {
            var response = await DurableTaskRuntimeHelper.RunAsync(task, inner);
            Assert.Equal(17, response.GetResult<int>());
            Assert.Same(outer, DurableExecutionContext.Current);
        });

        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task CancellationIsMonotonicIdempotentAndLateRegistrationsObserveIt()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("cancel"));
        var calls = 0;
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        });

        await Task.WhenAll(
            DurableTaskRuntimeHelper.RequestCancellationAsync(context),
            DurableTaskRuntimeHelper.RequestCancellationAsync(context));
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        });

        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationInvokesEveryCallbackAndAggregatesFailures()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("callback-failures"));
        var calls = 0;
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("first");
        });
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new ArgumentException("second");
        });

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => DurableTaskRuntimeHelper.RequestCancellationAsync(context));

        Assert.Equal(2, calls);
        Assert.Equal(2, exception.InnerExceptions.Count);
    }

    [Fact]
    public async Task CancellationCallbackCanRequestCancellationWithoutBlocking()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("reentrant-cancel"));
        Task? reentrantRequest = null;
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            reentrantRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
            Assert.True(reentrantRequest.IsCompletedSuccessfully);
            return new(reentrantRequest);
        });

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        Assert.NotNull(reentrantRequest);
        Assert.True(reentrantRequest.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NestedCancellationCallbackCanRequestOuterCancellationWithoutBlocking()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var outer = host.CreateContext(TaskId.CreateRoot("outer-cancel"));
        var inner = host.CreateContext(TaskId.CreateRoot("inner-cancel"));
        Task? nestedRequest = null;
        await outer.RegisterCancellationCallbackAsync(
            async _ => await DurableTaskRuntimeHelper.RequestCancellationAsync(inner));
        await inner.RegisterCancellationCallbackAsync(_ =>
        {
            nestedRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(outer);
            Assert.True(nestedRequest.IsCompletedSuccessfully);
            return new(nestedRequest);
        });

        await DurableTaskRuntimeHelper.RequestCancellationAsync(outer);

        Assert.True(inner.IsCancellationRequested);
        Assert.NotNull(nestedRequest);
        Assert.True(nestedRequest.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RegisterCancellationCallbackAsyncFlowsCausalityThroughTaskRun()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("task-run-first"));
        var second = host.CreateContext(TaskId.CreateRoot("task-run-second"));
        await first.RegisterCancellationCallbackAsync(
            async _ => await Task.Run(
                async () => await DurableTaskRuntimeHelper.RequestCancellationAsync(second)));
        await second.RegisterCancellationCallbackAsync(_ =>
        {
            var cycleClosingRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(cycleClosingRequest.IsCompletedSuccessfully);
            return new(cycleClosingRequest);
        });

        await DurableTaskRuntimeHelper.RequestCancellationAsync(first).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task SuppressedTaskRunDetachesCancellationDependency()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("suppressed-first"));
        var second = host.CreateContext(TaskId.CreateRoot("suppressed-second"));
        var reverseRequestCompletedSynchronously = true;
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            Task detachedRequest;
            using (ExecutionContext.SuppressFlow())
            {
                detachedRequest = Task.Run(
                    () => DurableTaskRuntimeHelper.RequestCancellationAsync(second));
            }

            await detachedRequest;
        });
        await second.RegisterCancellationCallbackAsync(_ =>
        {
            var reverseRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            reverseRequestCompletedSynchronously = reverseRequest.IsCompletedSuccessfully;
            return ValueTask.CompletedTask;
        });

        await DurableTaskRuntimeHelper.RequestCancellationAsync(first).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reverseRequestCompletedSynchronously);
        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task TokenCallbacksFollowStandardExecutionContextSemantics()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("execution-context"));
        var alreadyCanceled = host.CreateContext(TaskId.CreateRoot("already-canceled"));
        var ambient = new AsyncLocal<string>();
        string? safeObserved = null;
        string? unsafeObserved = null;
        string? immediateObserved = null;
        ambient.Value = "registration";
        using var safeRegistration = context.CancellationToken.Register(() =>
        {
            Assert.Null(DurableExecutionContext.Current);
            safeObserved = ambient.Value;
        });
        using var unsafeRegistration = context.CancellationToken.UnsafeRegister(_ =>
        {
            Assert.Null(DurableExecutionContext.Current);
            unsafeObserved = ambient.Value;
        }, null);
        ambient.Value = "request";

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await DurableTaskRuntimeHelper.RequestCancellationAsync(alreadyCanceled);
        ambient.Value = "immediate-registration";
        using var immediateRegistration = alreadyCanceled.CancellationToken.Register(() =>
        {
            Assert.Null(DurableExecutionContext.Current);
            immediateObserved = ambient.Value;
        });

        Assert.Equal("registration", safeObserved);
        Assert.Equal("request", unsafeObserved);
        Assert.Equal("immediate-registration", immediateObserved);
    }

    [Fact]
    public async Task TokenCallbackUsesCapturedSynchronizationContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("synchronization-context"));
        var synchronizationContext = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext? observed = null;
        CancellationTokenRegistration registration;
        try
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            registration = context.CancellationToken.Register(
                () =>
                {
                    Assert.Null(DurableExecutionContext.Current);
                    observed = SynchronizationContext.Current;
                },
                useSynchronizationContext: true);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (registration)
        {
            await DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        }

        Assert.Same(synchronizationContext, observed);
        Assert.Equal(1, synchronizationContext.SendCount);
    }

    [Fact]
    public async Task TokenCallbackExceptionsAreObservedFromSynchronousCancellation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("synchronous-token-errors"));
        var first = new InvalidOperationException("first");
        var second = new ArgumentException("second");
        using var firstRegistration = context.CancellationToken.Register(() => throw first);
        using var secondRegistration = context.CancellationToken.Register(() => throw second);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        Assert.True(cancellation.IsCompleted);
        var exception = await Assert.ThrowsAsync<AggregateException>(() => cancellation);
        Assert.Contains(first, exception.InnerExceptions);
        Assert.Contains(second, exception.InnerExceptions);
    }

    [Fact]
    public async Task UnrelatedExternalRequestDuringActiveCancellationAwaitsItsOwnCompletion()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var active = host.CreateContext(TaskId.CreateRoot("active"));
        var unrelated = host.CreateContext(TaskId.CreateRoot("unrelated"));
        var activeCallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActiveCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedCallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnrelatedCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("unrelated failure");
        await active.RegisterCancellationCallbackAsync(async _ =>
        {
            activeCallbackStarted.SetResult();
            await releaseActiveCallback.Task;
        });
        await unrelated.RegisterCancellationCallbackAsync(async _ =>
        {
            unrelatedCallbackStarted.SetResult();
            await releaseUnrelatedCallback.Task;
            throw expected;
        });

        var activeRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(active);
        await activeCallbackStarted.Task;
        var unrelatedRequest = Task.Run(
            () => DurableTaskRuntimeHelper.RequestCancellationAsync(unrelated));
        await unrelatedCallbackStarted.Task;

        Assert.False(unrelatedRequest.IsCompleted);
        releaseUnrelatedCallback.SetResult();
        var exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await unrelatedRequest.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(expected, Assert.Single(exception.InnerExceptions));
        Assert.False(activeRequest.IsCompleted);
        releaseActiveCallback.SetResult();
        await activeRequest;
    }

    [Fact]
    public async Task ExternalRequestAfterTokenCancellationStillAwaitsAndPropagatesErrors()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("after-token-cancel"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("durable callback failed");
        var tokenCallbackInvoked = false;
        using var tokenRegistration = context.CancellationToken.Register(
            () => tokenCallbackInvoked = true);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            callbackStarted.SetResult();
            await releaseCallback.Task;
            throw expected;
        });

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await callbackStarted.Task;
        Assert.True(tokenCallbackInvoked);
        Assert.Null(DurableExecutionContext.Current);

        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        Assert.Same(first, second);
        Assert.False(second.IsCompleted);

        releaseCallback.SetResult();
        foreach (var request in new[] { first, second })
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(async () => await request);
            Assert.Same(expected, Assert.Single(exception.InnerExceptions));
        }
    }

    [Fact]
    public async Task ConcurrentMutualCancellationCycleCompletes()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("mutual-first"));
        var second = host.CreateContext(TaskId.CreateRoot("mutual-second"));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginDependencies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDependencyAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            firstStarted.SetResult();
            await beginDependencies.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
            firstDependencyAdded.SetResult();
            await request;
        });
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            secondStarted.SetResult();
            await beginDependencies.Task;
            await firstDependencyAdded.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(request.IsCompletedSuccessfully);
            await request;
        });

        var firstCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
        var secondCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        beginDependencies.SetResult();
        await Task.WhenAll(firstCancellation, secondCancellation).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstCancellation.IsCompletedSuccessfully);
        Assert.True(secondCancellation.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ThreeContextCancellationCycleCompletes()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("cycle-first"));
        var second = host.CreateContext(TaskId.CreateRoot("cycle-second"));
        var third = host.CreateContext(TaskId.CreateRoot("cycle-third"));
        var beginDependencies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDependencyAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDependencyAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
            firstDependencyAdded.SetResult();
            await request;
        });
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            await firstDependencyAdded.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(third);
            secondDependencyAdded.SetResult();
            await request;
        });
        await third.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            await secondDependencyAdded.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(request.IsCompletedSuccessfully);
            await request;
        });

        var cancellations = new[]
        {
            DurableTaskRuntimeHelper.RequestCancellationAsync(first),
            DurableTaskRuntimeHelper.RequestCancellationAsync(second),
            DurableTaskRuntimeHelper.RequestCancellationAsync(third),
        };
        beginDependencies.SetResult();
        await Task.WhenAll(cancellations).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(cancellations, cancellation => Assert.True(cancellation.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task AcyclicCrossContextCancellationWaitsAndPropagatesErrors()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("acyclic-first"));
        var second = host.CreateContext(TaskId.CreateRoot("acyclic-second"));
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("second failed");
        await first.RegisterCancellationCallbackAsync(
            async _ => await DurableTaskRuntimeHelper.RequestCancellationAsync(second));
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            secondStarted.SetResult();
            await releaseSecond.Task;
            throw expected;
        });

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
        await secondStarted.Task;

        Assert.False(cancellation.IsCompleted);
        releaseSecond.SetResult();
        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await cancellation);
        Assert.Contains(expected, exception.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task ConcurrentExternalCancellationObserversShareCompletionAndErrors()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("concurrent-observers"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("observer failure");
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            callbackStarted.SetResult();
            await releaseCallback.Task;
            throw expected;
        });

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await callbackStarted.Task;
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        var third = DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.False(first.IsCompleted);
        releaseCallback.SetResult();
        foreach (var observer in new[] { first, second, third })
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(async () => await observer);
            Assert.Same(expected, Assert.Single(exception.InnerExceptions));
        }
    }

    [Fact]
    public async Task LateRegistrationJoinsActiveCancellationCompletionAndErrors()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("late-active"));
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailure = new InvalidOperationException("first late callback failed");
        var secondFailure = new ArgumentException("second late callback failed");
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task;
        });

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await blockerStarted.Task;
        var registration = await context.RegisterCancellationCallbackAsync(async _ =>
        {
            lateStarted.SetResult();
            await releaseLate.Task;
            throw firstFailure;
        });
        var secondRegistration = await context.RegisterCancellationCallbackAsync(_ => throw secondFailure);
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        Assert.Same(first, second);
        Assert.False(lateStarted.Task.IsCompleted);
        releaseBlocker.SetResult();
        await lateStarted.Task;
        Assert.False(first.IsCompleted);

        releaseLate.SetResult();
        foreach (var observer in new[] { first, second })
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(async () => await observer);
            Assert.Collection(
                exception.InnerExceptions,
                item => Assert.Same(firstFailure, item),
                item => Assert.Same(secondFailure, item));
        }

        await registration.DisposeAsync();
        await secondRegistration.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentLateCallbacksBreakMutualCancellationCycle()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("late-cycle-first"));
        var second = host.CreateContext(TaskId.CreateRoot("late-cycle-second"));
        var firstBlockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBlockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDependencyAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            firstBlockerStarted.SetResult();
            await releaseBlockers.Task;
        });
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            secondBlockerStarted.SetResult();
            await releaseBlockers.Task;
        });

        var firstCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
        var secondCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
        await Task.WhenAll(firstBlockerStarted.Task, secondBlockerStarted.Task);
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
            firstDependencyAdded.SetResult();
            await request;
        });
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            await firstDependencyAdded.Task;
            var cycleClosingRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(cycleClosingRequest.IsCompletedSuccessfully);
            await cycleClosingRequest;
        });

        releaseBlockers.SetResult();
        await Task.WhenAll(firstCancellation, secondCancellation).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstCancellation.IsCompletedSuccessfully);
        Assert.True(secondCancellation.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationCycleDoesNotHideSiblingCallbackErrors()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("failure-cycle-first"));
        var second = host.CreateContext(TaskId.CreateRoot("failure-cycle-second"));
        var beginDependencies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDependencyAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailure = new InvalidOperationException("first sibling failed");
        var secondFailure = new ArgumentException("second sibling failed");
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
            firstDependencyAdded.SetResult();
            await request;
        });
        await first.RegisterCancellationCallbackAsync(_ => throw firstFailure);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            await firstDependencyAdded.Task;
            await DurableTaskRuntimeHelper.RequestCancellationAsync(first);
        });
        await second.RegisterCancellationCallbackAsync(_ => throw secondFailure);

        var firstCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
        var secondCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
        beginDependencies.SetResult();
        var firstException = await Assert.ThrowsAsync<AggregateException>(
            async () => await firstCancellation.WaitAsync(TimeSpan.FromSeconds(10)));
        var secondException = await Assert.ThrowsAsync<AggregateException>(
            async () => await secondCancellation.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains(firstFailure, firstException.Flatten().InnerExceptions);
        Assert.Contains(secondFailure, firstException.Flatten().InnerExceptions);
        Assert.Contains(secondFailure, secondException.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task ReentrantCancellationDoesNotHideSiblingCallbackFailure()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("reentrant-failure"));
        var expected = new InvalidOperationException("sibling failure");
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
            Assert.True(request.IsCompletedSuccessfully);
            return new(request);
        });
        await context.RegisterCancellationCallbackAsync(_ => throw expected);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => DurableTaskRuntimeHelper.RequestCancellationAsync(context));

        Assert.Same(expected, Assert.Single(exception.InnerExceptions));
    }

    [Fact]
    public async Task OutsideCancellationCallerWaitsForActiveCallbackAndSharesCompletion()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("outside-cancel"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
            Assert.True(request.IsCompletedSuccessfully);
            callbackStarted.SetResult();
            await releaseCallback.Task;
        });

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await callbackStarted.Task;
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        Assert.Same(first, second);
        Assert.False(second.IsCompleted);
        releaseCallback.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposingSnapshottedCancellationRegistrationPreventsPendingInvocation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("dispose-pending"));
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingInvoked = false;
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            activeStarted.SetResult();
            await releaseActive.Task;
        });
        var pending = await context.RegisterCancellationCallbackAsync(_ =>
        {
            pendingInvoked = true;
            return ValueTask.CompletedTask;
        });

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await activeStarted.Task;
        await pending.DisposeAsync();
        releaseActive.SetResult();
        await cancellation;

        Assert.False(pendingInvoked);
    }

    [Fact]
    public async Task DisposingActiveThrowingCancellationRegistrationWaitsForInvocation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("dispose-active"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = await context.RegisterCancellationCallbackAsync(async _ =>
        {
            callbackStarted.SetResult();
            await releaseCallback.Task;
            throw new InvalidOperationException("callback failure");
        });

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await callbackStarted.Task;
        var disposal = registration.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        releaseCallback.SetResult();
        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await cancellation);
        await disposal;

        Assert.Single(exception.InnerExceptions);
        Assert.IsType<InvalidOperationException>(exception.InnerExceptions[0]);
        Assert.True(disposal.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationRegistrationCanDisposeItselfWithoutBlocking()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("self-dispose"));
        IAsyncDisposable? registration = null;
        var invoked = false;
        registration = await context.RegisterCancellationCallbackAsync(async _ =>
        {
            var disposal = registration!.DisposeAsync();
            Assert.True(disposal.IsCompletedSuccessfully);
            await disposal;
            invoked = true;
        });

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await registration.DisposeAsync();

        Assert.True(invoked);
    }

    [Fact]
    public async Task LateCancellationCallbackRunsInDurableContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("late-callback"));
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context);

        await context.RegisterCancellationCallbackAsync(token =>
        {
            Assert.Same(context, DurableExecutionContext.Current);
            Assert.True(token.IsCancellationRequested);
            return ValueTask.CompletedTask;
        });

        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task PostCompletionCallbackHasIndependentObservableCompletion()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("post-completion"));
        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        await cancellation;
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("post-completion failure");
        var calls = 0;

        var registration = await context.RegisterCancellationCallbackAsync(async token =>
        {
            Assert.Same(context, DurableExecutionContext.Current);
            Assert.True(token.IsCancellationRequested);
            Interlocked.Increment(ref calls);
            callbackStarted.SetResult();
            await releaseCallback.Task;
            throw expected;
        });
        await callbackStarted.Task;

        var disposal = registration.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Same(cancellation, DurableTaskRuntimeHelper.RequestCancellationAsync(context));
        Assert.True(cancellation.IsCompletedSuccessfully);

        releaseCallback.SetResult();
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(async () => await disposal));
        Assert.Same(
            expected,
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await registration.DisposeAsync()));
        Assert.Equal(1, calls);
        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task RegistrationRacingCancellationCompletionLandsOnExactlyOneSide()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var host = new TestHost(DateTimeOffset.UnixEpoch);
            var context = host.CreateContext(TaskId.CreateRoot($"completion-race-{iteration}"));
            var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new InvalidOperationException($"race failure {iteration}");
            var calls = 0;
            await context.RegisterCancellationCallbackAsync(async _ =>
            {
                blockerStarted.SetResult();
                await releaseBlocker.Task;
            });
            var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
            await blockerStarted.Task;

            var registrationTask = Task.Run(
                async () => await context.RegisterCancellationCallbackAsync(async _ =>
                {
                    Interlocked.Increment(ref calls);
                    callbackStarted.SetResult();
                    await releaseCallback.Task;
                    throw expected;
                }));
            var releaseTask = Task.Run(releaseBlocker.SetResult);
            var registration = await registrationTask;
            await releaseTask;
            await callbackStarted.Task;
            var joinedCancellation = !cancellation.IsCompleted;

            releaseCallback.SetResult();
            if (joinedCancellation)
            {
                var exception = await Assert.ThrowsAsync<AggregateException>(async () => await cancellation);
                Assert.Same(expected, Assert.Single(exception.InnerExceptions));
                await registration.DisposeAsync();
            }
            else
            {
                Assert.True(cancellation.IsCompletedSuccessfully);
                Assert.Same(
                    expected,
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        async () => await registration.DisposeAsync()));
            }

            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task RegistrationRacingCancellationIsAlwaysObserved()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var host = new TestHost(DateTimeOffset.UnixEpoch);
            var context = host.CreateContext(TaskId.CreateRoot($"race-{iteration}"));
            var calls = 0;
            await Task.WhenAll(
                Task.Run(async () =>
                {
                    await context.RegisterCancellationCallbackAsync(_ =>
                    {
                        Interlocked.Increment(ref calls);
                        return ValueTask.CompletedTask;
                    });
                }),
                Task.Run(() => DurableTaskRuntimeHelper.RequestCancellationAsync(context)));

            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public void ResponseStatesAreConsistent()
    {
        AssertResponse(DurableTaskResponse.Pending, DurableTaskResponseKind.Pending, DurableTaskStatus.Pending, false);
        AssertResponse(DurableTaskResponse.Subscribed, DurableTaskResponseKind.Subscribed, DurableTaskStatus.Pending, false);
        AssertResponse(DurableTaskResponse.Completed, DurableTaskResponseKind.CompletedSuccessfully, DurableTaskStatus.CompletedSuccessfully, true);
        AssertResponse(DurableTaskResponse.Canceled, DurableTaskResponseKind.Canceled, DurableTaskStatus.Canceled, true);
        AssertResponse(DurableTaskResponse.FromException(new InvalidOperationException()), DurableTaskResponseKind.Failed, DurableTaskStatus.Failed, true);
        Assert.Throws<InvalidOperationException>(() => DurableTaskResponse.Pending.GetResult<int>());
        Assert.Throws<InvalidOperationException>(() => DurableTaskResponse.Pending.Result);
        Assert.Throws<InvalidOperationException>(() => DurableTaskResponse.Subscribed.GetResult<int>());
        Assert.Throws<InvalidOperationException>(() => DurableTaskResponse.Subscribed.Result);
        Assert.Throws<OperationCanceledException>(() => DurableTaskResponse.Canceled.GetResult<int>());
    }

    [Fact]
    public void SuccessfulNullResultsRetainTheirDeclaredType()
    {
        var referenceResult = DurableTaskResponse.FromResult<string?>(null);
        var nullableResult = DurableTaskResponse.FromResult<int?>(null);

        Assert.Null(referenceResult.GetResult<string?>());
        Assert.Null(referenceResult.GetResult<object?>());
        Assert.Null(nullableResult.GetResult<int?>());
        Assert.Throws<InvalidCastException>(() => referenceResult.GetResult<int?>());
        Assert.Throws<InvalidCastException>(() => nullableResult.GetResult<long?>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RuntimeHelperConvertsSynchronousAndAsynchronousHostFailuresToResponses(
        bool asynchronous,
        bool canceled)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch)
        {
            DelayException = canceled
                ? new OperationCanceledException("host canceled")
                : new InvalidOperationException("host failed"),
            DelayFailureIsAsynchronous = asynchronous,
        };
        var context = host.CreateContext(TaskId.CreateRoot($"failure-{asynchronous}-{canceled}"));

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.Zero), context);

        Assert.Equal(canceled ? DurableTaskStatus.Canceled : DurableTaskStatus.Failed, response.Status);
        Assert.Equal(canceled ? DurableTaskResponseKind.Canceled : DurableTaskResponseKind.Failed, response.ResponseKind);
        Assert.Equal(host.DelayException, response.Exception);
    }

    [Fact]
    public void TaskIdSeparatesSegmentsFromPaths()
    {
        var root = TaskId.CreateRoot("tenant/workflow");
        var child = root.Child("step/one");
        var parsed = TaskId.Parse(@"tenant\/workflow/step\/one");

        Assert.Equal(@"tenant\/workflow", root.ToString());
        Assert.Equal(@"tenant\/workflow/step\/one", child.ToString());
        Assert.Equal(child, parsed);
        Assert.True(root.IsParentOf(child));
        Assert.Equal(root, child.Parent());
        Assert.NotEqual(TaskId.Parse("tenant/workflow"), root);
        Assert.Equal(child.GetHashCode(), parsed.GetHashCode());
        Assert.Equal(child, (TaskId)(string)child);
        Assert.Throws<ArgumentException>(() => TaskId.CreateRoot(""));
        Assert.Throws<ArgumentException>(() => root.Child(""));
    }

    [Fact]
    public void TaskIdNoneRoundTripsThroughStringAndSpanContracts()
    {
        Assert.Equal(string.Empty, TaskId.None.ToString());
        Assert.Equal(TaskId.None, TaskId.Parse(string.Empty));
        Assert.True(TaskId.TryParse(string.Empty, provider: null, out var fromString));
        Assert.Equal(TaskId.None, fromString);
        Assert.False(TaskId.TryParse((string?)null, provider: null, out _));
        Assert.Equal(TaskId.None, TaskId.Parse(ReadOnlySpan<char>.Empty, provider: null));
        Assert.True(TaskId.TryParse(ReadOnlySpan<char>.Empty, provider: null, out var fromSpan));
        Assert.Equal(TaskId.None, fromSpan);

        Span<char> destination = stackalloc char[1];
        Assert.True(TaskId.None.TryFormat(destination, out var charsWritten, default, provider: null));
        Assert.Equal(0, charsWritten);
    }

    [Fact]
    public void PublicFactoriesValidateDelegatesAndTaskCollections()
    {
        Assert.Throws<ArgumentNullException>(() => DurableTask.Run((Action<CancellationToken>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.Run((Func<CancellationToken, int>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.Run((Func<CancellationToken, Task>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.Run((Func<CancellationToken, Task<int>>)null!));
        Assert.Throws<ArgumentNullException>(
            () => DurableTask.Run<object?>((Action<object?, CancellationToken>)null!, state: null));
        Assert.Throws<ArgumentNullException>(
            () => DurableTask.Run<object?, int>((Func<object?, CancellationToken, int>)null!, state: null));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAll((IReadOnlyList<DurableTask>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAll((IReadOnlyList<DurableTask<int>>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAny((IReadOnlyList<DurableTask>)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.WhenAny([]));
    }

    [Fact]
    public void DefaultPollingOptionsUsesDocumentedTimeout()
        => Assert.Equal(PollingOptions.DefaultPollTimeout, default(PollingOptions).PollTimeout);

    private static void AssertResponse(
        DurableTaskResponse response,
        DurableTaskResponseKind kind,
        DurableTaskStatus status,
        bool completed)
    {
        Assert.Equal(kind, response.ResponseKind);
        Assert.Equal(status, response.Status);
        Assert.Equal(completed, response.IsCompleted);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int SendCount { get; private set; }

        public override void Send(SendOrPostCallback d, object? state)
        {
            SendCount++;
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}

[Trait("Category", "BVT")]
public class SchedulingTests
{
    [Fact]
    public async Task ExistingRootIdReattachesToRecordedResponse()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var definition = host.CreateRootDefinition<string>(async context =>
        {
            await Task.Yield();
            return DurableTaskResponse.FromResult(context.TaskId.ToString());
        });

        var first = await definition.ScheduleAsync("stable-root");
        var second = await definition.ScheduleAsync("stable-root");

        Assert.Equal("stable-root", await first);
        Assert.Equal("stable-root", await second);
        Assert.Equal(1, host.ExecutionCount);
    }

    [Fact]
    public async Task RootSchedulingRequiresAnExplicitId()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var definition = host.CreateRootDefinition<object?>(_ => ValueTask.FromResult(DurableTaskResponse.Completed));

        await Assert.ThrowsAsync<InvalidOperationException>(AwaitWithoutIdAsync);

        async Task AwaitWithoutIdAsync() => _ = await definition;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GeneratedChildIdsAreDisjointFromNumericNamesAndStableAcrossReplay(bool explicitFirst)
    {
        var first = await ExecuteAsync(new TestHost(DateTimeOffset.UnixEpoch), explicitFirst);
        var replay = await ExecuteAsync(new TestHost(DateTimeOffset.UnixEpoch), explicitFirst);

        Assert.Equal(
        [
            TaskId.Parse("root/$child-1"),
            TaskId.Parse("root/1"),
        ],
            first);
        Assert.Equal(first, replay);

        static async Task<IReadOnlyList<TaskId>> ExecuteAsync(TestHost host, bool explicitFirst)
        {
            var root = host.CreateRootDefinition<object?>(async _ =>
            {
                if (explicitFirst)
                {
                    await DurableTask.Run(static _ => { }).WithId("1");
                    await DurableTask.Run(static _ => { });
                }
                else
                {
                    await DurableTask.Run(static _ => { });
                    await DurableTask.Run(static _ => { }).WithId("1");
                }

                return DurableTaskResponse.Completed;
            });

            var scheduled = await root.ScheduleAsync("root");
            await ((ScheduledTask)scheduled).WaitAsync();
            return host.EntryIds.Where(id => id != TaskId.CreateRoot("root")).ToArray();
        }
    }

    [Theory]
    [InlineData("$child-1")]
    [InlineData("$when-all-1")]
    public async Task ExplicitChildNamesRejectGeneratedNamespace(string name)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("root"));

        var response = await DurableTaskRuntimeHelper.RunAsync(Definition(), context);

        var exception = Assert.IsType<ArgumentException>(response.Exception);
        Assert.StartsWith("Explicit child names", exception.Message);

        async DurableTask Definition()
            => await DurableTask.Run(static _ => { }).WithId(name);
    }

    [Fact]
    public async Task CombinatorsUseStableIndexesAndWhenAnyLeavesLosersRunning()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var slowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = host.CreateRootDefinition<TaskId>(async context =>
        {
            var winnerId = await DurableTask.WhenAny(
            [
                DurableTask.Run(async _ => await slowCompletion.Task),
                DurableTask.Run(static _ => Task.CompletedTask),
            ]);
            return DurableTaskResponse.FromResult(winnerId);
        });

        var scheduled = await root.ScheduleAsync("root");
        var winningId = await scheduled;

        Assert.Equal(TaskId.Parse("root/$when-any-1/1"), winningId);
        Assert.True(host.Contains(TaskId.Parse("root/$when-any-1/0")));
        Assert.False(host.IsCancellationRequested(TaskId.Parse("root/$when-any-1/0")));
        slowCompletion.SetResult();
    }

    [Fact]
    public async Task CombinatorsSnapshotInputsAtDefinitionCreation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var inputs = new List<DurableTask<int>> { DurableTask.FromResult(1) };
        var whenAll = DurableTask.WhenAll(inputs);
        var whenAny = DurableTask.WhenAny(inputs);
        inputs.Clear();
        var root = host.CreateRootDefinition<(IReadOnlyList<TaskId> All, TaskId Any)>(async _ =>
        {
            var all = await whenAll;
            var any = await whenAny;
            return DurableTaskResponse.FromResult((all, any));
        });

        var scheduled = await root.ScheduleAsync("snapshot");
        var result = await scheduled;

        Assert.Equal(TaskId.Parse("snapshot/$when-all-1/0"), Assert.Single(result.All));
        Assert.Equal(TaskId.Parse("snapshot/$when-any-2/0"), result.Any);
    }

    [Fact]
    public async Task RepeatedCombinatorsAllocateDistinctOperationAndChildIds()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<IReadOnlyList<TaskId>>(async _ =>
        {
            var first = await DurableTask.WhenAll(
            [
                DurableTask.Run(static _ => { }),
                DurableTask.Run(static _ => { }),
            ]);
            var second = await DurableTask.WhenAll(
            [
                DurableTask.Run(static _ => { }),
                DurableTask.Run(static _ => { }),
            ]);
            return DurableTaskResponse.FromResult<IReadOnlyList<TaskId>>([.. first, .. second]);
        });

        var result = await await root.ScheduleAsync("root");

        Assert.Equal(
        [
            TaskId.Parse("root/$when-all-1/0"),
            TaskId.Parse("root/$when-all-1/1"),
            TaskId.Parse("root/$when-all-2/0"),
            TaskId.Parse("root/$when-all-2/1"),
        ],
            result);
        Assert.Equal(4, result.Distinct().Count());
        Assert.All(result, id => Assert.IsType<TaskId>(id));
    }

    [Fact]
    public async Task NestedCombinatorsAllocateChildrenBeneathTheirScheduledOperation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<IReadOnlyList<TaskId>>(async _ =>
        {
            var ids = await DurableTask.WhenAll(
            [
                DurableTask.WhenAll(
                [
                    DurableTask.Run(static _ => { }),
                    DurableTask.Run(static _ => { }),
                ]),
                DurableTask.WhenAll(
                [
                    DurableTask.Run(static _ => { }),
                ]),
            ]);
            return DurableTaskResponse.FromResult(ids);
        });

        var result = await await root.ScheduleAsync("root");

        Assert.Equal(
        [
            TaskId.Parse("root/$when-all-1/0"),
            TaskId.Parse("root/$when-all-1/1"),
        ],
            result);
        Assert.True(host.Contains(TaskId.Parse("root/$when-all-1/0/$when-all-1/0")));
        Assert.True(host.Contains(TaskId.Parse("root/$when-all-1/0/$when-all-1/1")));
        Assert.True(host.Contains(TaskId.Parse("root/$when-all-1/1/$when-all-1/0")));
    }

    [Fact]
    public async Task CombinatorIdsAndWinnerAreStableAcrossReplay()
    {
        var first = await ExecuteAsync(new TestHost(DateTimeOffset.UnixEpoch));
        var replay = await ExecuteAsync(new TestHost(DateTimeOffset.UnixEpoch));

        Assert.Equal(TaskId.Parse("root/$when-any-1/1"), first.Winner);
        Assert.Equal(first.Winner, replay.Winner);
        Assert.Equal(first.Ids, replay.Ids);

        static async Task<(TaskId Winner, IReadOnlyList<TaskId> Ids)> ExecuteAsync(TestHost host)
        {
            var loser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var root = host.CreateRootDefinition<TaskId>(async _ =>
            {
                var winner = await DurableTask.WhenAny(
                [
                    DurableTask.Run(async _ => await loser.Task),
                    DurableTask.Run(static _ => Task.CompletedTask),
                ]);
                return DurableTaskResponse.FromResult(winner);
            });

            var winner = await await root.ScheduleAsync("root");
            var ids = host.EntryIds;
            loser.SetResult();
            return (winner, ids);
        }
    }

    [Fact]
    public async Task WhenAnyWinnerIsPersistedAcrossReplay()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = host.CreateContext(TaskId.CreateRoot("replay"));
        var candidates = new[]
        {
            TaskId.Parse("replay/$when-any-1/0"),
            TaskId.Parse("replay/$when-any-1/1"),
        };
        host.GetEntry(candidates[0]).StartOnce(async () =>
        {
            await first.Task;
            return DurableTaskResponse.Completed;
        });
        host.GetEntry(candidates[1]).StartOnce(async () =>
        {
            await second.Task;
            return DurableTaskResponse.Completed;
        });
        second.SetResult();

        var decisionId = TaskId.Parse("replay/$when-any-1/$winner");
        var winner = await context.SelectForTestAsync(decisionId, candidates);
        first.SetResult();
        var replayedWinner = await host.CreateContext(TaskId.CreateRoot("replay"))
            .SelectForTestAsync(decisionId, candidates);

        Assert.Equal(candidates[1], winner);
        Assert.Equal(winner, replayedWinner);
    }

    [Fact]
    public async Task CancelRequestIsDistinctFromCancelingWait()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = host.CreateRootDefinition<object?>(async context =>
        {
            await completion.Task;
            return DurableTaskResponse.Completed;
        });
        var scheduled = await definition.ScheduleAsync("wait");
        using var waitCancellation = new CancellationTokenSource();
        waitCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduled.GetResponseAsync(waitCancellation.Token));
        Assert.False(host.IsCancellationRequested(TaskId.CreateRoot("wait")));

        await scheduled.CancelAsync();
        await scheduled.CancelAsync();
        Assert.True(host.IsCancellationRequested(TaskId.CreateRoot("wait")));
        completion.SetResult();
    }

    [Fact]
    public async Task ScheduledWhenAnyHonorsExternalCancellationAndDrainsLosingWaits()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await firstCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        var secondDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await secondCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        ScheduledTask first = await firstDefinition.ScheduleAsync("first");
        ScheduledTask second = await secondDefinition.ScheduleAsync("second");
        using var cancellation = new CancellationTokenSource();

        var whenAny = ScheduledTask.WhenAny([first, second], cancellation.Token);
        await Task.WhenAll(
            host.GetEntry(first.Id).WaitStarted.Task,
            host.GetEntry(second.Id).WaitStarted.Task);
        Assert.Equal(2, host.ActiveWaitCount);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await whenAny);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, host.ActiveWaitCount);
        Assert.False(host.IsCancellationRequested(first.Id));
        Assert.False(host.IsCancellationRequested(second.Id));
        Assert.False(firstCompletion.Task.IsCompleted);
        Assert.False(secondCompletion.Task.IsCompleted);

        firstCompletion.SetResult();
        secondCompletion.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledWhenAnyReturnsWinnerWithFailedOrCanceledDurableResponse(bool canceled)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var loserCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception terminalException = canceled
            ? new OperationCanceledException("durable cancellation")
            : new InvalidOperationException("durable failure");
        var winnerDefinition = host.CreateRootDefinition<object?>(
            _ => ValueTask.FromResult(DurableTaskResponse.FromException(terminalException)));
        var loserDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await loserCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        ScheduledTask expectedWinner = await winnerDefinition.ScheduleAsync("winner");
        ScheduledTask loser = await loserDefinition.ScheduleAsync("loser");

        var winner = await ScheduledTask.WhenAny([expectedWinner, loser]);
        var response = await winner.GetResponseAsync();

        Assert.Same(expectedWinner, winner);
        Assert.Same(terminalException, response.Exception);
        Assert.Equal(canceled ? DurableTaskStatus.Canceled : DurableTaskStatus.Failed, response.Status);
        Assert.Equal(0, host.ActiveWaitCount);
        Assert.False(host.IsCancellationRequested(loser.Id));
        Assert.False(loserCompletion.Task.IsCompleted);

        loserCompletion.SetResult();
    }

    [Fact]
    public async Task ScheduledWhenAnyPropagatesHostWaitFailureAndDrainsLosingWaits()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await firstCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        var secondDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await secondCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        ScheduledTask first = await firstDefinition.ScheduleAsync("transport-failure");
        ScheduledTask second = await secondDefinition.ScheduleAsync("transport-loser");
        var expected = new InvalidOperationException("host wait failed");
        host.GetEntry(first.Id).WaitException = expected;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ScheduledTask.WhenAny([first, second]));

        Assert.Same(expected, exception);
        Assert.Equal(0, host.ActiveWaitCount);
        Assert.False(host.IsCancellationRequested(first.Id));
        Assert.False(host.IsCancellationRequested(second.Id));
        Assert.False(firstCompletion.Task.IsCompleted);
        Assert.False(secondCompletion.Task.IsCompleted);

        firstCompletion.SetResult();
        secondCompletion.SetResult();
    }

    [Fact]
    public async Task ScheduledWhenAnyDrainsStartedWaitWhenLaterWaitThrowsSynchronously()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await firstCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        var secondDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await secondCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        ScheduledTask first = await firstDefinition.ScheduleAsync("started-before-failure");
        ScheduledTask second = await secondDefinition.ScheduleAsync("synchronous-wait-failure");
        var expected = new InvalidOperationException("synchronous host wait failure");
        host.GetEntry(second.Id).WaitException = expected;
        host.GetEntry(second.Id).WaitExceptionIsSynchronous = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ScheduledTask.WhenAny([first, second]));

        Assert.Same(expected, exception);
        Assert.True(host.GetEntry(first.Id).WaitStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(0, host.ActiveWaitCount);
        Assert.False(host.IsCancellationRequested(first.Id));
        Assert.False(host.IsCancellationRequested(second.Id));
        Assert.False(firstCompletion.Task.IsCompleted);
        Assert.False(secondCompletion.Task.IsCompleted);

        firstCompletion.SetResult();
        secondCompletion.SetResult();
    }

    [Fact]
    public async Task GenericAndNonGenericWaitsObserveTheSameSuccessfulResponse()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var definition = host.CreateRootDefinition<int>(
            _ => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(7)));
        var generic = await definition.ScheduleAsync("wait-result");
        ScheduledTask nonGeneric = generic;

        await nonGeneric.WaitAsync();
        var response = await nonGeneric.GetResponseAsync();
        var result = await generic;

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(7, response.GetResult<int>());
        Assert.Equal(7, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericAndNonGenericWaitsPropagateTheSameTerminalError(bool canceled)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        Exception expected = canceled
            ? new OperationCanceledException("canceled")
            : new InvalidOperationException("failed");
        var definition = host.CreateRootDefinition<int>(
            _ => ValueTask.FromResult(DurableTaskResponse.FromException(expected)));
        var generic = await definition.ScheduleAsync($"terminal-{canceled}");
        ScheduledTask nonGeneric = generic;

        var nonGenericException = await Assert.ThrowsAnyAsync<Exception>(
            async () => await nonGeneric.WaitAsync());
        var genericException = await Assert.ThrowsAnyAsync<Exception>(
            async () => _ = await generic);

        Assert.Same(expected, nonGenericException);
        Assert.Same(expected, genericException);
        Assert.Equal(
            canceled ? DurableTaskStatus.Canceled : DurableTaskStatus.Failed,
            (await nonGeneric.GetResponseAsync()).Status);
    }

    [Fact]
    public async Task DefaultPollingOptionsArePassedToTheHost()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = host.CreateRootDefinition<object?>(async _ =>
        {
            await completion.Task;
            return DurableTaskResponse.Completed;
        });
        var scheduled = await definition.ScheduleAsync("poll");

        var status = await scheduled.GetStatusAsync();

        Assert.Equal(DurableTaskStatus.Pending, status);
        Assert.Equal(PollingOptions.DefaultPollTimeout, host.LastPollTimeout);
        completion.SetResult();
    }

    [Fact]
    public async Task RootIdStringIsOneLogicalSegmentNotAPath()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var definition = host.CreateRootDefinition<string>(
            context => ValueTask.FromResult<DurableTaskResponse>(
                DurableTaskResponse.FromResult(context.TaskId.ToString())));

        var scheduled = await definition.ScheduleAsync("tenant/workflow");

        Assert.Equal(@"tenant\/workflow", scheduled.Id.ToString());
        Assert.Equal(@"tenant\/workflow", await scheduled);
        Assert.NotEqual(TaskId.Parse("tenant/workflow"), scheduled.Id);
    }

}

internal sealed class TestHost(DateTimeOffset utcNow)
{
    private readonly ConcurrentDictionary<TaskId, Entry> _entries = new();
    private readonly ConcurrentDictionary<TaskId, TaskId> _decisions = new();
    private int _activeWaitCount;
    public int ExecutionCount;
    public DateTimeOffset? LastDelayDueTime { get; private set; }
    public CancellationToken LastDelayCancellationToken { get; private set; }
    public TimeSpan? LastPollTimeout { get; private set; }
    public Exception? DelayException { get; init; }
    public bool DelayFailureIsAsynchronous { get; init; }

    public TestContext CreateContext(TaskId taskId) => new(this, taskId, utcNow);
    public RootDefinition<TResult> CreateRootDefinition<TResult>(Func<TestContext, ValueTask<DurableTaskResponse>> run) => new(this, run);
    public bool Contains(TaskId id) => _entries.ContainsKey(id);
    public bool IsCancellationRequested(TaskId id) => _entries.TryGetValue(id, out var entry) && entry.CancellationRequested;
    public IReadOnlyList<TaskId> EntryIds => _entries.Keys.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
    public int ActiveWaitCount => Volatile.Read(ref _activeWaitCount);

    public async Task RunWithAmbientAsync(
        DurableExecutionContext ambient,
        Func<Task> callback)
    {
        var wrapper = DurableTask.Run(async _ => await callback());
        var response = await DurableTaskRuntimeHelper.RunAsync(wrapper, ambient);
        if (response.Exception is { } exception)
        {
            throw exception;
        }
    }

    internal ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId id,
        DurableTask definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = _entries.GetOrAdd(id, static (taskId, state) => state.CreateEntry(taskId), this);
        entry.StartOnce(() => RunDefinitionAsync(definition, id));
        return new(entry);
    }

    internal async Task<DurableTaskResponse> RunDefinitionAsync(DurableTask definition, TaskId id)
    {
        Interlocked.Increment(ref ExecutionCount);
        return await DurableTaskRuntimeHelper.RunAsync(definition, CreateContext(id));
    }

    internal ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId id,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        LastDelayDueTime = dueTime;
        LastDelayCancellationToken = cancellationToken;
        if (DelayException is { } exception)
        {
            return DelayFailureIsAsynchronous
                ? new(Task.FromException<DurableTaskResponse>(exception))
                : throw exception;
        }

        return new(DurableTaskResponse.Completed);
    }

    internal Entry GetEntry(TaskId id) => _entries.GetOrAdd(id, static (taskId, state) => state.CreateEntry(taskId), this);

    internal async ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        if (_decisions.TryGetValue(decisionId, out var recorded))
        {
            return recorded;
        }

        var waits = candidates.Select(id => GetEntry(id).WaitAsync(cancellationToken).AsTask()).ToArray();
        var completed = await Task.WhenAny(waits);
        var winner = candidates[Array.IndexOf(waits, completed)];
        return _decisions.GetOrAdd(decisionId, winner);
    }

    private Entry CreateEntry(TaskId id) => new(this, id);

    internal sealed class Entry(TestHost host, TaskId id) : IScheduledTaskHandle
    {
        private readonly object _lock = new();
        private Task<DurableTaskResponse>? _response;
        public TaskId TaskId => id;
        public bool CancellationRequested { get; private set; }
        public Exception? WaitException { get; set; }
        public bool WaitExceptionIsSynchronous { get; set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StartOnce(Func<Task<DurableTaskResponse>> start)
        {
            lock (_lock)
            {
                _response ??= start();
            }
        }

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            if (WaitExceptionIsSynchronous && WaitException is { } exception)
            {
                throw exception;
            }

            return WaitAsyncCore(cancellationToken);
        }

        private async ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref host._activeWaitCount);
            WaitStarted.TrySetResult();
            try
            {
                if (WaitException is { } exception)
                {
                    return await Task.FromException<DurableTaskResponse>(exception);
                }

                return await (_response ?? Task.FromResult(DurableTaskResponse.Pending)).WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref host._activeWaitCount);
            }
        }

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            host.LastPollTimeout = options.PollTimeout;
            var response = _response;
            return response is { IsCompletedSuccessfully: true }
                ? new(response.Result)
                : new(DurableTaskResponse.Pending);
        }

        public ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationRequested = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class TestContext(TestHost host, TaskId id, DateTimeOffset utcNow) : DurableExecutionContext(id)
{
    public override DateTimeOffset UtcNow => utcNow;
    protected override ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
        => host.ScheduleChildAsync(taskId, taskDefinition, cancellationToken);
    protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
        => host.ScheduleDelayAsync(taskId, dueTime, cancellationToken);
    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => host.GetEntry(taskId);
    protected override ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
        => host.SelectCompletionAsync(decisionId, candidates, cancellationToken);

    public ValueTask<TaskId> SelectForTestAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken = default)
        => SelectCompletionAsync(decisionId, candidates, cancellationToken);
}

internal sealed class RootDefinition<TResult>(
    TestHost host,
    Func<TestContext, ValueTask<DurableTaskResponse>> run) : DurableTask<TResult>, ISchedulableTask
{
    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = host.GetEntry(taskId);
        entry.StartOnce(async () =>
        {
            Interlocked.Increment(ref host.ExecutionCount);
            var context = host.CreateContext(taskId);
            return await DurableTaskRuntimeHelper.RunAsync(new CallbackDurableTask(run), context);
        });
        return entry.PollAsync(default, cancellationToken);
    }

    public IScheduledTaskHandle GetHandle(TaskId taskId) => host.GetEntry(taskId);

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => throw new InvalidOperationException("Root definitions are scheduled by their host.");
}

internal sealed class CallbackDurableTask(Func<TestContext, ValueTask<DurableTaskResponse>> callback) : DurableTask
{
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        try
        {
            return await callback((TestContext)context);
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}
