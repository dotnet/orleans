using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
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
    public void DefaultNonGenericBuilderOperationsRequireStart()
    {
        var builder = default(DurableTaskMethodBuilder);

        AssertBuilderNotStarted(() => _ = builder.Task);
        AssertBuilderNotStarted(() => builder.SetException(new InvalidOperationException()));
        AssertBuilderNotStarted(() => builder.SetResult());
        AssertBuilderNotStarted(() => AwaitOnCompleted(builder));
        AssertBuilderNotStarted(() => AwaitUnsafeOnCompleted(builder));
    }

    [Fact]
    public void DefaultGenericBuilderOperationsRequireStart()
    {
        var builder = default(DurableTaskMethodBuilder<int>);

        AssertBuilderNotStarted(() => _ = builder.Task);
        AssertBuilderNotStarted(() => builder.SetException(new InvalidOperationException()));
        AssertBuilderNotStarted(() => builder.SetResult(42));
        AssertBuilderNotStarted(() => AwaitOnCompleted(builder));
        AssertBuilderNotStarted(() => AwaitUnsafeOnCompleted(builder));
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
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);

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
        }, Xunit.TestContext.Current.CancellationToken);

        await Task.WhenAll(
            DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken),
            DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken));
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationStateIsPublishedBeforeTokenObserversRun()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("cancel-state"));
        bool? observedCancellationState = null;
        using var registration = context.CancellationToken.Register(
            () => observedCancellationState = context.IsCancellationRequested);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);

        Assert.True(observedCancellationState);
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
        }, Xunit.TestContext.Current.CancellationToken);
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new ArgumentException("second");
        }, Xunit.TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken));

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
        }, Xunit.TestContext.Current.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);

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
        await outer.RegisterCancellationCallbackAsync(async _ => await DurableTaskRuntimeHelper.RequestCancellationAsync(inner), Xunit.TestContext.Current.CancellationToken);
        await inner.RegisterCancellationCallbackAsync(_ =>
        {
            nestedRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(outer);
            Assert.True(nestedRequest.IsCompletedSuccessfully);
            return new(nestedRequest);
        }, Xunit.TestContext.Current.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(outer, Xunit.TestContext.Current.CancellationToken);

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
        await first.RegisterCancellationCallbackAsync(async _ => await Task.Run(
                async () => await DurableTaskRuntimeHelper.RequestCancellationAsync(second)), Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(_ =>
        {
            var cycleClosingRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(cycleClosingRequest.IsCompletedSuccessfully);
            return new(cycleClosingRequest);
        }, Xunit.TestContext.Current.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task CompilerLoweredTaskFlowsCancellationCausalityAcrossOrdinaryAwait()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("durable-await-first"));
        var second = host.CreateContext(TaskId.CreateRoot("durable-await-second"));
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            var response = await DurableTaskRuntimeHelper.RunAsync(RequestSecondCancellationAsync(), first);
            Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(_ =>
        {
            var cycleClosingRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(cycleClosingRequest.IsCompletedSuccessfully);
            return new(cycleClosingRequest);
        }, Xunit.TestContext.Current.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);

        async DurableTask RequestSecondCancellationAsync()
        {
            await Task.Yield();
            await DurableTaskRuntimeHelper.RequestCancellationAsync(second);
        }
    }

    [Fact]
    public async Task CompilerLoweredTasksFlowExecutionContextAcrossSafeAwaiter()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("safe-awaiter"));
        var ambient = new AsyncLocal<string>();
        string? nonGenericObserved = null;
        ambient.Value = "non-generic";

        var nonGenericResponse = await DurableTaskRuntimeHelper.RunAsync(ObserveNonGenericAsync(), context);
        ambient.Value = "generic";
        var genericResponse = await DurableTaskRuntimeHelper.RunAsync(ObserveGenericAsync(), context);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, nonGenericResponse.Status);
        Assert.Equal("non-generic", nonGenericObserved);
        Assert.Equal("generic", genericResponse.GetResult<string>());
        Assert.Equal("generic", ambient.Value);

        async DurableTask ObserveNonGenericAsync()
        {
            await NonCriticalYieldAwaitable.Instance;
            Assert.Same(context, DurableExecutionContext.Current);
            nonGenericObserved = ambient.Value;
        }

        async DurableTask<string?> ObserveGenericAsync()
        {
            await NonCriticalYieldAwaitable.Instance;
            Assert.Same(context, DurableExecutionContext.Current);
            return ambient.Value;
        }
    }

    [Fact]
    public async Task CompilerLoweredTaskHonorsSuppressedExecutionContextFlow()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("suppressed-durable-await"));
        var ambient = new AsyncLocal<string>();
        string? observedBeforeAwait = null;
        string? observedAfterAwait = null;
        ambient.Value = "caller";
        var task = ObserveAsync();
        ValueTask<DurableTaskResponse> execution;

        using (ExecutionContext.SuppressFlow())
        {
            execution = DurableTaskRuntimeHelper.RunAsync(task, context);
        }

        var response = await execution;

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal("caller", observedBeforeAwait);
        Assert.Null(observedAfterAwait);
        Assert.Equal("caller", ambient.Value);

        async DurableTask ObserveAsync()
        {
            observedBeforeAwait = ambient.Value;
            await Task.Yield();
            Assert.Same(context, DurableExecutionContext.Current);
            observedAfterAwait = ambient.Value;
        }
    }

    [Fact]
    public async Task CompilerLoweredContinuationRestoresCompletingThreadAmbientContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var outer = host.CreateContext(TaskId.CreateRoot("outer-completing-thread"));
        var inner = host.CreateContext(TaskId.CreateRoot("inner-completing-thread"));
        var awaitable = new ControlledUnsafeAwaitable<int>(42);
        Task<DurableTaskResponse>? innerExecution = null;
        var outerTask = DurableTask.Run(_ =>
        {
            Assert.Same(outer, DurableExecutionContext.Current);
            using (ExecutionContext.SuppressFlow())
            {
                innerExecution = DurableTaskRuntimeHelper.RunAsync(InnerAsync(), inner).AsTask();
            }

            Assert.NotNull(innerExecution);
            Assert.False(innerExecution.IsCompleted);
            awaitable.Complete();
            Assert.Same(outer, DurableExecutionContext.Current);
        });

        var outerResponse = await DurableTaskRuntimeHelper.RunAsync(outerTask, outer);
        var innerResponse = await innerExecution!;

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, outerResponse.Status);
        Assert.Equal(42, innerResponse.GetResult<int>());
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask<int> InnerAsync()
        {
            Assert.Same(inner, DurableExecutionContext.Current);
            var result = await awaitable;
            Assert.Same(inner, DurableExecutionContext.Current);
            return result;
        }
    }

    [Fact]
    public async Task CompilerLoweredTasksFlowExecutionContextAcrossSuccessiveSafeAndUnsafeAwaits()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("successive-awaits"));
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        var safeObservations = new List<string?>();

        var nonGenericResponse = await DurableTaskRuntimeHelper.RunAsync(ObserveSafeAsync(), context);
        var genericResponse = await DurableTaskRuntimeHelper.RunAsync(ObserveUnsafeAsync(), context);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, nonGenericResponse.Status);
        Assert.Equal(["caller", "safe-second"], safeObservations);
        Assert.Equal("unsafe-second", genericResponse.GetResult<string?>());
        Assert.Equal("caller", ambient.Value);

        async DurableTask ObserveSafeAsync()
        {
            await NonCriticalYieldAwaitable.Instance;
            safeObservations.Add(ambient.Value);
            ambient.Value = "safe-second";
            await NonCriticalYieldAwaitable.Instance;
            safeObservations.Add(ambient.Value);
        }

        async DurableTask<string?> ObserveUnsafeAsync()
        {
            await CriticalYieldAwaitable.Instance;
            Assert.Equal("caller", ambient.Value);
            ambient.Value = "unsafe-second";
            await CriticalYieldAwaitable.Instance;
            return ambient.Value;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilerLoweredGenericTaskHonorsSuppressedExecutionContextFlow(bool safeAwaiter)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot($"suppressed-generic-{safeAwaiter}"));
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        ValueTask<DurableTaskResponse> execution;

        using (ExecutionContext.SuppressFlow())
        {
            execution = DurableTaskRuntimeHelper.RunAsync(ObserveAsync(), context);
        }

        var response = await execution;

        Assert.Null(response.GetResult<string?>());
        Assert.Equal("caller", ambient.Value);

        async DurableTask<string?> ObserveAsync()
        {
            if (safeAwaiter)
            {
                await NonCriticalYieldAwaitable.Instance;
            }
            else
            {
                await CriticalYieldAwaitable.Instance;
            }

            Assert.Same(context, DurableExecutionContext.Current);
            return ambient.Value;
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CompilerLoweredTasksPreserveFaultAndCancellationBeforeAndAfterSuspension(
        bool generic,
        bool suspend,
        bool canceled)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot($"completion-{generic}-{suspend}-{canceled}"));
        Exception expected = canceled
            ? new OperationCanceledException("canceled")
            : new InvalidOperationException("failed");

        var response = await DurableTaskRuntimeHelper.RunAsync(
            generic ? GenericAsync() : NonGenericAsync(),
            context);

        Assert.Equal(canceled ? DurableTaskStatus.Canceled : DurableTaskStatus.Failed, response.Status);
        Assert.Same(expected, response.Exception);

        async DurableTask NonGenericAsync()
        {
            if (suspend)
            {
                await CriticalYieldAwaitable.Instance;
            }

            throw expected;
        }

        async DurableTask<int> GenericAsync()
        {
            if (suspend)
            {
                await CriticalYieldAwaitable.Instance;
            }

            throw expected;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilerLoweredTaskDefinitionCanExecuteOnlyOnce(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var task = generic ? GenericAsync() : NonGenericAsync();
        var firstContext = host.CreateContext(TaskId.CreateRoot($"first-{generic}"));
        var secondContext = host.CreateContext(TaskId.CreateRoot($"second-{generic}"));

        var first = await DurableTaskRuntimeHelper.RunAsync(task, firstContext);
        var second = await DurableTaskRuntimeHelper.RunAsync(task, secondContext);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, first.Status);
        var exception = Assert.IsType<InvalidOperationException>(second.Exception);
        Assert.Equal("A deferred durable task definition can execute only once.", exception.Message);

        async DurableTask NonGenericAsync()
        {
            await Task.CompletedTask;
        }

        async DurableTask<int> GenericAsync()
        {
            await Task.CompletedTask;
            return 42;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedCompilerLoweredTaskReleasesCapturedState(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var (task, capturedState) = CreateCompilerLoweredTaskWithCapturedState(generic);
        var context = host.CreateContext(TaskId.CreateRoot($"release-state-{generic}"));

        var response = await DurableTaskRuntimeHelper.RunAsync(task, context);
        ForceFullCollection();

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.False(capturedState.IsAlive);
        GC.KeepAlive(task);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompilerLoweredTaskCompletionDoesNotRunAwaitContinuationInline(
        bool generic,
        bool faulted)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot($"continuation-{generic}-{faulted}"));
        var gate = new TaskCompletionSource();
        var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("failed");
        var execution = DurableTaskRuntimeHelper.RunAsync(
            generic ? GenericAsync() : NonGenericAsync(),
            context).AsTask();
        var completionThread = Environment.CurrentManagedThreadId;
        var completing = false;
        var ranInline = false;
        execution.GetAwaiter().UnsafeOnCompleted(() =>
        {
            ranInline = completing && Environment.CurrentManagedThreadId == completionThread;
            continuationRan.TrySetResult();
        });

        completing = true;
        gate.SetResult();
        completing = false;

        await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        var response = await execution;

        Assert.False(ranInline);
        Assert.Equal(faulted ? DurableTaskStatus.Failed : DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(faulted ? expected : null, response.Exception);

        async DurableTask NonGenericAsync()
        {
            await gate.Task;
            if (faulted)
            {
                throw expected;
            }
        }

        async DurableTask<int> GenericAsync()
        {
            await gate.Task;
            if (faulted)
            {
                throw expected;
            }

            return 42;
        }
    }

    [Fact]
    public void StartedBuildersRejectNullStateMachineAndException()
    {
        var stateMachine = default(NoopStateMachine);
        var nonGeneric = DurableTaskMethodBuilder.Create();
        nonGeneric.Start(ref stateMachine);
        var generic = DurableTaskMethodBuilder<int>.Create();
        generic.Start(ref stateMachine);

        Assert.Throws<ArgumentNullException>(() => nonGeneric.SetStateMachine(null!));
        Assert.Throws<ArgumentNullException>(() => nonGeneric.SetException(null!));
        Assert.Throws<ArgumentNullException>(() => generic.SetStateMachine(null!));
        Assert.Throws<ArgumentNullException>(() => generic.SetException(null!));
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
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(_ =>
        {
            var reverseRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            reverseRequestCompletedSynchronously = reverseRequest.IsCompletedSuccessfully;
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.False(reverseRequestCompletedSynchronously);
        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationRequestTokenAbandonsOnlyCallerWait()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("caller-cancellation"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            callbackStarted.TrySetResult();
            await releaseCallback.Task;
        }, Xunit.TestContext.Current.CancellationToken);
        using var callerCancellation = new CancellationTokenSource();

        var canceledObserver = DurableTaskRuntimeHelper.RequestCancellationAsync(context, callerCancellation.Token);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        var completingObserver = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        callerCancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledObserver.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
        Assert.True(context.IsCancellationRequested);
        Assert.False(completingObserver.IsCompleted);

        releaseCallback.TrySetResult();
        await completingObserver.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RegisterCancellationCallbackValidatesCallbackAndCallerToken()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("registration-validation"));
        var invoked = false;
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        Assert.Throws<ArgumentNullException>(
            () => context.RegisterCancellationCallbackAsync(null!, Xunit.TestContext.Current.CancellationToken));
        var exception = Assert.Throws<OperationCanceledException>(
            () => context.RegisterCancellationCallbackAsync(
                _ =>
                {
                    invoked = true;
                    return ValueTask.CompletedTask;
                },
                callerCancellation.Token));
        Assert.Equal(callerCancellation.Token, exception.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        Assert.False(invoked);
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

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        await DurableTaskRuntimeHelper.RequestCancellationAsync(alreadyCanceled, Xunit.TestContext.Current.CancellationToken);
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
            await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
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

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);

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
        }, Xunit.TestContext.Current.CancellationToken);
        await unrelated.RegisterCancellationCallbackAsync(async _ =>
        {
            unrelatedCallbackStarted.SetResult();
            await releaseUnrelatedCallback.Task;
            throw expected;
        }, Xunit.TestContext.Current.CancellationToken);

        var activeRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(active, Xunit.TestContext.Current.CancellationToken);
        await activeCallbackStarted.Task;
        var unrelatedRequest = Task.Run(() => DurableTaskRuntimeHelper.RequestCancellationAsync(unrelated), Xunit.TestContext.Current.CancellationToken);
        await unrelatedCallbackStarted.Task;

        Assert.False(unrelatedRequest.IsCompleted);
        releaseUnrelatedCallback.SetResult();
        var exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await unrelatedRequest.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));

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
        var uncancelableToken = new CancellationToken(canceled: false);
        using var tokenRegistration = context.CancellationToken.Register(
            () => tokenCallbackInvoked = true);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            callbackStarted.SetResult();
            await releaseCallback.Task;
            throw expected;
        }, Xunit.TestContext.Current.CancellationToken);

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
        await callbackStarted.Task;
        Assert.True(tokenCallbackInvoked);
        Assert.Null(DurableExecutionContext.Current);

        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
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
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            secondStarted.SetResult();
            await beginDependencies.Task;
            await firstDependencyAdded.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(request.IsCompletedSuccessfully);
            await request;
        }, Xunit.TestContext.Current.CancellationToken);

        var firstCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken);
        var secondCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(second, Xunit.TestContext.Current.CancellationToken);
        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        beginDependencies.SetResult();
        await Task.WhenAll(firstCancellation, secondCancellation).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

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
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            await firstDependencyAdded.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(third);
            secondDependencyAdded.SetResult();
            await request;
        }, Xunit.TestContext.Current.CancellationToken);
        await third.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            await secondDependencyAdded.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(request.IsCompletedSuccessfully);
            await request;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellations = new[]
        {
            DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken),
            DurableTaskRuntimeHelper.RequestCancellationAsync(second, Xunit.TestContext.Current.CancellationToken),
            DurableTaskRuntimeHelper.RequestCancellationAsync(third, Xunit.TestContext.Current.CancellationToken),
        };
        beginDependencies.SetResult();
        await Task.WhenAll(cancellations).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

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
        await first.RegisterCancellationCallbackAsync(async _ => await DurableTaskRuntimeHelper.RequestCancellationAsync(second), Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            secondStarted.SetResult();
            await releaseSecond.Task;
            throw expected;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken);
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
        var uncancelableToken = new CancellationToken(canceled: false);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            callbackStarted.SetResult();
            await releaseCallback.Task;
            throw expected;
        }, Xunit.TestContext.Current.CancellationToken);

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
        await callbackStarted.Task;
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
        var third = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);

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
        var uncancelableToken = new CancellationToken(canceled: false);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task;
        }, Xunit.TestContext.Current.CancellationToken);

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
        await blockerStarted.Task;
        var registration = await context.RegisterCancellationCallbackAsync(async _ =>
        {
            lateStarted.SetResult();
            await releaseLate.Task;
            throw firstFailure;
        }, Xunit.TestContext.Current.CancellationToken);
        var secondRegistration = await context.RegisterCancellationCallbackAsync(_ => throw secondFailure, Xunit.TestContext.Current.CancellationToken);
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);

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
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            secondBlockerStarted.SetResult();
            await releaseBlockers.Task;
        }, Xunit.TestContext.Current.CancellationToken);

        var firstCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken);
        var secondCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(second, Xunit.TestContext.Current.CancellationToken);
        await Task.WhenAll(firstBlockerStarted.Task, secondBlockerStarted.Task);
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(second);
            firstDependencyAdded.SetResult();
            await request;
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            await firstDependencyAdded.Task;
            var cycleClosingRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(cycleClosingRequest.IsCompletedSuccessfully);
            await cycleClosingRequest;
        }, Xunit.TestContext.Current.CancellationToken);

        releaseBlockers.SetResult();
        await Task.WhenAll(firstCancellation, secondCancellation).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

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
        }, Xunit.TestContext.Current.CancellationToken);
        await first.RegisterCancellationCallbackAsync(_ => throw firstFailure, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            await beginDependencies.Task;
            await firstDependencyAdded.Task;
            await DurableTaskRuntimeHelper.RequestCancellationAsync(first);
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(_ => throw secondFailure, Xunit.TestContext.Current.CancellationToken);

        var firstCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken);
        var secondCancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(second, Xunit.TestContext.Current.CancellationToken);
        beginDependencies.SetResult();
        var firstException = await Assert.ThrowsAsync<AggregateException>(
            async () => await firstCancellation.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));
        var secondException = await Assert.ThrowsAsync<AggregateException>(
            async () => await secondCancellation.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));

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
        }, Xunit.TestContext.Current.CancellationToken);
        await context.RegisterCancellationCallbackAsync(_ => throw expected, Xunit.TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken));

        Assert.Same(expected, Assert.Single(exception.InnerExceptions));
    }

    [Fact]
    public async Task OutsideCancellationCallerWaitsForActiveCallbackAndSharesCompletion()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("outside-cancel"));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uncancelableToken = new CancellationToken(canceled: false);
        await context.RegisterCancellationCallbackAsync(async _ =>
        {
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
            Assert.True(request.IsCompletedSuccessfully);
            callbackStarted.SetResult();
            await releaseCallback.Task;
        }, Xunit.TestContext.Current.CancellationToken);

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);
        await callbackStarted.Task;
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context, uncancelableToken);

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
        }, Xunit.TestContext.Current.CancellationToken);
        var pending = await context.RegisterCancellationCallbackAsync(_ =>
        {
            pendingInvoked = true;
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
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
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
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
        }, Xunit.TestContext.Current.CancellationToken);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        await registration.DisposeAsync();

        Assert.True(invoked);
    }

    [Fact]
    public async Task LateCancellationCallbackRunsInDurableContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("late-callback"));
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);

        await context.RegisterCancellationCallbackAsync(token =>
        {
            Assert.Same(context, DurableExecutionContext.Current);
            Assert.True(token.IsCancellationRequested);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task PostCompletionCallbackHasIndependentObservableCompletion()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("post-completion"));
        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
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
        }, Xunit.TestContext.Current.CancellationToken);
        Assert.True(callbackStarted.Task.IsCompletedSuccessfully);
        await callbackStarted.Task;

        var disposal = registration.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Same(
            cancellation,
            DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken));
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
            }, Xunit.TestContext.Current.CancellationToken);
            var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
            await blockerStarted.Task;

            var registrationTask = Task.Run(
                async () => await context.RegisterCancellationCallbackAsync(async _ =>
                {
                    Interlocked.Increment(ref calls);
                    callbackStarted.SetResult();
                    await releaseCallback.Task;
                    throw expected;
                }));
            var releaseTask = Task.Run(releaseBlocker.SetResult, Xunit.TestContext.Current.CancellationToken);
            var registration = await registrationTask;
            await releaseTask;
            await callbackStarted.Task;

            releaseCallback.SetResult();
            var cancellationException = await Record.ExceptionAsync(async () => await cancellation);
            var disposalException = await Record.ExceptionAsync(async () => await registration.DisposeAsync());
            if (cancellationException is AggregateException aggregateException)
            {
                Assert.Same(expected, Assert.Single(aggregateException.InnerExceptions));
                Assert.Null(disposalException);
            }
            else
            {
                Assert.Null(cancellationException);
                Assert.Same(expected, Assert.IsType<InvalidOperationException>(disposalException));
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
                    }, Xunit.TestContext.Current.CancellationToken);
                }, Xunit.TestContext.Current.CancellationToken),
                Task.Run(
                    () => DurableTaskRuntimeHelper.RequestCancellationAsync(
                        context,
                        Xunit.TestContext.Current.CancellationToken),
                    Xunit.TestContext.Current.CancellationToken));

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

    [Fact]
    public void ResponseFactoriesRejectNullExceptionsAndPreserveExactException()
    {
        var failure = new InvalidOperationException("failed");
        var cancellation = new OperationCanceledException("canceled");

        Assert.Same(failure, DurableTaskResponse.FromException(failure).Exception);
        Assert.Same(cancellation, DurableTaskResponse.FromCanceled(cancellation).Exception);
        Assert.Same(cancellation, DurableTaskResponse.FromException(cancellation).Exception);
        Assert.Throws<ArgumentNullException>(() => DurableTaskResponse.FromException(null!));
        Assert.Throws<ArgumentNullException>(() => DurableTaskResponse.FromCanceled(null!));
        Assert.Throws<ArgumentNullException>(() => new ExceptionDurableTaskResponse(null!));
        Assert.Throws<ArgumentNullException>(() => new CanceledDurableTaskResponse(null!));
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
        Assert.True(TaskId.TryParse(string.Empty, out var fromStringWithoutProvider));
        Assert.Equal(TaskId.None, fromStringWithoutProvider);
        Assert.True(TaskId.TryParse(string.Empty, provider: null, out var fromString));
        Assert.Equal(TaskId.None, fromString);
        Assert.False(TaskId.TryParse((string?)null, provider: null, out _));
        Assert.Equal(TaskId.None, TaskId.Parse(ReadOnlySpan<char>.Empty));
        Assert.True(TaskId.TryParse(ReadOnlySpan<char>.Empty, out var fromSpanWithoutProvider));
        Assert.Equal(TaskId.None, fromSpanWithoutProvider);
        Assert.True(TaskId.TryParse(ReadOnlySpan<char>.Empty, provider: null, out var fromSpan));
        Assert.Equal(TaskId.None, fromSpan);

        Span<char> destination = stackalloc char[1];
        Assert.True(TaskId.None.TryFormat(destination, out var charsWritten, default, provider: null));
        Assert.Equal(0, charsWritten);
    }

    [Theory]
    [InlineData(@"\")]
    [InlineData(@"\x")]
    [InlineData("/root")]
    [InlineData("root/")]
    [InlineData("root//child")]
    public void TaskIdRejectsMalformedEscapedPathsAcrossStringAndSpanApis(string value)
    {
        Assert.False(TaskId.TryParse(value, out _));
        Assert.False(TaskId.TryParse(value.AsSpan(), out _));
        Assert.Throws<FormatException>(() => TaskId.Parse(value));
        Assert.Throws<FormatException>(() => ParseSpan(value));

        static TaskId ParseSpan(string input) => TaskId.Parse(input.AsSpan());
    }

    [Fact]
    public void TaskIdTryFormatHonorsExactAndOneShortBuffersIncludingEscapes()
    {
        var taskId = TaskId.CreateRoot(@"tenant/workflow").Child(@"step\one");
        var expected = taskId.ToString();
        var exact = new char[expected.Length];

        Assert.True(taskId.TryFormat(exact, out var exactCharsWritten, default, provider: null));
        Assert.Equal(expected.Length, exactCharsWritten);
        Assert.Equal(expected, new string(exact));
        for (var length = 0; length < expected.Length; length++)
        {
            var shortBuffer = new char[length];
            Assert.False(taskId.TryFormat(shortBuffer, out var shortCharsWritten, default, provider: null));
            Assert.Equal(0, shortCharsWritten);
        }
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
        Assert.Throws<ArgumentNullException>(
            () => DurableTask.Run<object?>((Func<object?, CancellationToken, Task>)null!, state: null));
        Assert.Throws<ArgumentNullException>(
            () => DurableTask.Run<object?, int>((Func<object?, CancellationToken, Task<int>>)null!, state: null));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAll((IReadOnlyList<DurableTask>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAll((IReadOnlyList<DurableTask<int>>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAny((IReadOnlyList<DurableTask>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAny((IReadOnlyList<DurableTask<int>>)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.WhenAny([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.WhenAny<int>([]));
    }

    [Fact]
    public void DefaultPollingOptionsUsesDocumentedTimeout()
        => Assert.Equal(PollingOptions.DefaultPollTimeout, default(PollingOptions).PollTimeout);

    [Fact]
    public void DelayRejectsNegativeButAcceptsZeroDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.Delay(TimeSpan.FromTicks(-1)));
        Assert.NotNull(DurableTask.Delay(TimeSpan.Zero));
    }

    [Fact]
    public void PollingOptionsRejectNegativeAndAcceptZeroTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new PollingOptions { PollTimeout = TimeSpan.FromTicks(-1) });

        var options = new PollingOptions { PollTimeout = TimeSpan.Zero };
        Assert.Equal(TimeSpan.Zero, options.PollTimeout);
    }

    [Fact]
    public void TaskIdSelfHierarchyIsReflexive()
    {
        var root = TaskId.CreateRoot("root");
        var child = root.Child("child");

        Assert.True(root.IsAncestorOf(root));
        Assert.True(root.IsDescendantOf(root));
        Assert.False(root.IsParentOf(root));
        Assert.False(root.IsChildOf(root));
        Assert.True(child.IsAncestorOf(child));
        Assert.True(child.IsDescendantOf(child));
        Assert.False(child.IsParentOf(child));
        Assert.False(child.IsChildOf(child));
    }

    [Fact]
    public void TaskIdRootParentIsNone()
    {
        var root = TaskId.CreateRoot("root");

        Assert.Equal(TaskId.None, root.Parent());
    }

    [Fact]
    public void TaskIdNoneCannotCreateChild()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TaskId.None.Child("child"));

        Assert.Equal("A child identifier requires a non-empty parent.", exception.Message);
    }

    [Fact]
    public void CompletedResponseTypedGetResultThrows()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => DurableTaskResponse.Completed.GetResult<int>());

        Assert.Equal("The completed task has no result value.", exception.Message);
    }

    [Fact]
    public async Task RuntimeHelperNullGuardsReportParameterNames()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("runtime-helper-guards"));
        var task = DurableTask.Delay(TimeSpan.Zero);

        var taskException = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await DurableTaskRuntimeHelper.RunAsync(null!, context));
        var contextException = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await DurableTaskRuntimeHelper.RunAsync(task, null!));
        var cancellationContextException = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DurableTaskRuntimeHelper.RequestCancellationAsync(null!, Xunit.TestContext.Current.CancellationToken));

        Assert.Equal("task", taskException.ParamName);
        Assert.Equal("context", contextException.ParamName);
        Assert.Equal("context", cancellationContextException.ParamName);
    }

    [Fact]
    public async Task NestedCancellationCallbackCanDisposeAncestorRegistrationWithoutBlocking()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var outer = host.CreateContext(TaskId.CreateRoot("outer-dispose"));
        var inner = host.CreateContext(TaskId.CreateRoot("inner-dispose"));
        var outerCallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ancestorDisposalCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerCallbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable? outerRegistration = null;
        outerRegistration = await outer.RegisterCancellationCallbackAsync(async _ =>
        {
            outerCallbackStarted.SetResult();
            await DurableTaskRuntimeHelper.RequestCancellationAsync(inner);
            outerCallbackCompleted.SetResult();
        }, Xunit.TestContext.Current.CancellationToken);
        await inner.RegisterCancellationCallbackAsync(async _ =>
        {
            var disposal = outerRegistration.DisposeAsync();
            Assert.True(disposal.IsCompletedSuccessfully);
            await disposal;
            ancestorDisposalCompleted.SetResult();
        }, Xunit.TestContext.Current.CancellationToken);
        var outerStartedWait = WaitForPhaseAsync(outerCallbackStarted.Task, "outer callback start");
        var ancestorDisposalWait = WaitForPhaseAsync(ancestorDisposalCompleted.Task, "ancestor registration disposal");
        var outerCompletedWait = WaitForPhaseAsync(outerCallbackCompleted.Task, "outer callback completion");

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(outer, Xunit.TestContext.Current.CancellationToken);
        var cancellationWait = WaitForPhaseAsync(cancellation, "outer cancellation completion");
        await Task.WhenAll(outerStartedWait, ancestorDisposalWait, outerCompletedWait, cancellationWait);

        Assert.True(outer.IsCancellationRequested);
        Assert.True(inner.IsCancellationRequested);
        Assert.True(cancellation.IsCompletedSuccessfully);

        async Task WaitForPhaseAsync(Task task, string phase)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {phase}; outer cancellation requested: {outer.IsCancellationRequested}; inner cancellation requested: {inner.IsCancellationRequested}.",
                    exception);
            }
        }
    }

    [Fact]
    public void DurableExecutionContextRejectsNoneTaskId()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);

        var exception = Assert.Throws<ArgumentException>(
            () => new TestContext(host, TaskId.None, DateTimeOffset.UnixEpoch));

        Assert.Equal("taskId", exception.ParamName);
        Assert.Equal(
            "A durable execution requires an explicit task identifier. (Parameter 'taskId')",
            exception.Message);
    }

    [Fact]
    public void TaskIdTryParseAcceptsValidStringAndSpan()
    {
        foreach (var expected in new[]
        {
            TaskId.CreateRoot("root"),
            TaskId.CreateRoot("tenant/workflow").Child(@"step\one"),
        })
        {
            var canonical = expected.ToString();

            Assert.True(TaskId.TryParse(canonical, provider: null, out var fromString));
            Assert.True(TaskId.TryParse(canonical.AsSpan(), provider: null, out var fromSpan));
            Assert.Equal(expected, fromString);
            Assert.Equal(expected, fromSpan);
            Assert.Equal(fromString, fromSpan);
        }
    }

    [Fact]
    public void TaskIdProviderToStringMatchesCanonicalFormat()
    {
        var taskId = TaskId.CreateRoot("tenant/workflow").Child(@"step\one");
        var canonical = @"tenant\/workflow/step\\one";

        Assert.Equal(canonical, taskId.ToString());
        Assert.Equal(canonical, taskId.ToString("G", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal(canonical, (string)taskId);
    }

    [Fact]
    public void TaskIdObjectEqualsRejectsNullAndOtherTypes()
    {
        var taskId = TaskId.CreateRoot("root").Child("child");
        object equivalent = TaskId.Parse("root/child");

        Assert.True(taskId.Equals(equivalent));
        Assert.False(taskId.Equals(null));
        Assert.False(taskId.Equals("root/child"));
        Assert.False(taskId.Equals(new object()));
    }

    [Fact]
    public void TaskIdEqualityAndHashCodeAgreeForEquivalentValues()
    {
        var hierarchical = TaskId.CreateRoot("root").Child("child");
        var equivalent = TaskId.Parse(hierarchical.ToString());
        var singleSegment = TaskId.CreateRoot("root/child");

        Assert.Equal(hierarchical, equivalent);
        Assert.True(hierarchical == equivalent);
        Assert.False(hierarchical != equivalent);
        Assert.Equal(hierarchical.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(hierarchical, singleSegment);
        Assert.True(hierarchical != singleSegment);
        Assert.False(hierarchical == singleSegment);
        Assert.NotEqual(hierarchical, TaskId.None);
        Assert.NotEqual(TaskId.None, hierarchical);
        Assert.Equal(0, TaskId.None.GetHashCode());
    }

    [Fact]
    public void TaskIdTryFormatWritesExactBufferAndRejectsShortBuffer()
    {
        var taskId = TaskId.CreateRoot("tenant/workflow").Child(@"step\one");
        var canonical = @"tenant\/workflow/step\\one";
        var exact = new char[canonical.Length];
        var shortBuffer = new char[canonical.Length - 1];

        Assert.True(taskId.TryFormat(exact, out var exactCharsWritten, "G", provider: null));
        Assert.Equal(canonical.Length, exactCharsWritten);
        Assert.Equal(canonical, new string(exact));
        Assert.False(taskId.TryFormat(shortBuffer, out var shortCharsWritten, "G", provider: null));
        Assert.Equal(0, shortCharsWritten);
    }

    [Theory]
    [InlineData("", @"\", @"\x", "/root", "root/", "root//child")]
    public void TaskIdParseRejectsEmptyAndMalformedHierarchies(
        string empty,
        string truncatedEscape,
        string invalidEscape,
        string leadingSeparator,
        string trailingSeparator,
        string emptySegment)
    {
        Assert.Equal(TaskId.None, TaskId.Parse(empty, provider: null));
        Assert.Equal(TaskId.None, TaskId.Parse(empty.AsSpan(), provider: null));
        Assert.True(TaskId.TryParse(empty, provider: null, out var emptyFromString));
        Assert.True(TaskId.TryParse(empty.AsSpan(), provider: null, out var emptyFromSpan));
        Assert.Equal(TaskId.None, emptyFromString);
        Assert.Equal(TaskId.None, emptyFromSpan);

        foreach (var malformed in new[]
        {
            truncatedEscape,
            invalidEscape,
            leadingSeparator,
            trailingSeparator,
            emptySegment,
        })
        {
            Assert.False(TaskId.TryParse(malformed, provider: null, out var fromString));
            Assert.False(TaskId.TryParse(malformed.AsSpan(), provider: null, out var fromSpan));
            Assert.Equal(TaskId.None, fromString);
            Assert.Equal(TaskId.None, fromSpan);

            var stringException = Assert.Throws<FormatException>(() => TaskId.Parse(malformed, provider: null));
            var spanException = Assert.Throws<FormatException>(() => ParseSpan(malformed));
            Assert.Equal("The task identifier is not a valid escaped hierarchical path.", stringException.Message);
            Assert.Equal(stringException.Message, spanException.Message);
        }

        static TaskId ParseSpan(string value) => TaskId.Parse(value.AsSpan(), provider: null);
    }

    [Theory]
    [InlineData(DurableTaskResponseKind.CompletedSuccessfully, DurableTaskStatus.CompletedSuccessfully, true, false)]
    [InlineData(DurableTaskResponseKind.CompletedSuccessfully, DurableTaskStatus.CompletedSuccessfully, true, true)]
    [InlineData(DurableTaskResponseKind.Failed, DurableTaskStatus.Failed, true, false)]
    [InlineData(DurableTaskResponseKind.Canceled, DurableTaskStatus.Canceled, true, false)]
    [InlineData(DurableTaskResponseKind.Pending, DurableTaskStatus.Pending, false, false)]
    [InlineData(DurableTaskResponseKind.Subscribed, DurableTaskStatus.Pending, false, false)]
    public void ResponseKindsExposeExpectedStatusAndResultType(
        DurableTaskResponseKind expectedKind,
        DurableTaskStatus expectedStatus,
        bool expectedCompleted,
        bool genericSuccess)
    {
        DurableTaskResponse response = expectedKind switch
        {
            DurableTaskResponseKind.CompletedSuccessfully when genericSuccess => DurableTaskResponse.FromResult(42),
            DurableTaskResponseKind.CompletedSuccessfully => DurableTaskResponse.Completed,
            DurableTaskResponseKind.Failed => DurableTaskResponse.FromException(new InvalidOperationException("failed")),
            DurableTaskResponseKind.Canceled => DurableTaskResponse.FromCanceled(new OperationCanceledException("canceled")),
            DurableTaskResponseKind.Pending => DurableTaskResponse.Pending,
            DurableTaskResponseKind.Subscribed => DurableTaskResponse.Subscribed,
            _ => throw new InvalidOperationException($"Unexpected test response kind '{expectedKind}'."),
        };

        Assert.Equal(expectedKind, response.ResponseKind);
        Assert.Equal(expectedStatus, response.Status);
        Assert.Equal(expectedCompleted, response.IsCompleted);
        Assert.Equal(genericSuccess ? typeof(int) : null, response.ResultType);
    }

    [Fact]
    public void NonGenericSuccessResponseReturnsWithoutResult()
    {
        var response = DurableTaskResponse.Completed;

        Assert.Same(SuccessDurableTaskResponse.Instance, response);
        Assert.Null(response.Result);
        Assert.Null(response.ResultType);
        Assert.Null(response.Exception);
        var exception = Assert.Throws<InvalidOperationException>(() => response.GetResult<int>());
        Assert.Equal("The completed task has no result value.", exception.Message);
    }

    [Fact]
    public void GenericSuccessResponseReturnsValueAndResultType()
    {
        var response = DurableTaskResponse.FromResult(42);

        Assert.Equal(42, response.TypedResult);
        Assert.Equal(42, response.Result);
        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(42, response.GetResult<object>());
        Assert.Equal(typeof(int), response.ResultType);
        Assert.Null(response.Exception);
        var exception = Assert.Throws<InvalidCastException>(() => response.GetResult<long>());
        Assert.Equal("The durable task result is 'System.Int32', not 'System.Int64'.", exception.Message);
    }

    [Fact]
    public void GenericSuccessResponseSupportsNullResult()
    {
        var response = DurableTaskResponse.FromResult<string?>(null);

        Assert.Null(response.TypedResult);
        Assert.Null(response.Result);
        Assert.Null(response.GetResult<string?>());
        Assert.Null(response.GetResult<object?>());
        Assert.Equal(typeof(string), response.ResultType);
        Assert.Null(response.Exception);
        var exception = Assert.Throws<InvalidCastException>(() => response.GetResult<int?>());
        Assert.Equal("The durable task result is 'System.String', not 'System.Nullable`1[System.Int32]'.", exception.Message);
    }

    [Fact]
    public void FailureResponseRethrowsOriginalException()
    {
        var failure = CreateResponseFailure();
        var response = DurableTaskResponse.FromException(failure);

        Assert.Same(failure, response.Exception);
        Assert.Null(response.ResultType);
        var thrown = Assert.Throws<InvalidOperationException>(() => _ = response.Result);
        Assert.Same(failure, thrown);
        Assert.Equal("phase-one failure", thrown.Message);
        Assert.Contains(nameof(ThrowResponseFailure), thrown.StackTrace);
    }

    [Fact]
    public void GenericFailureResponseRethrowsOriginalException()
    {
        var failure = CreateResponseFailure();
        var response = DurableTaskResponse.FromException(failure);

        var thrown = Assert.Throws<InvalidOperationException>(() => response.GetResult<int>());
        Assert.Same(failure, thrown);
        Assert.Equal("phase-one failure", thrown.Message);
        Assert.Contains(nameof(ThrowResponseFailure), thrown.StackTrace);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Equal(DurableTaskResponseKind.Failed, response.ResponseKind);
    }

    [Fact]
    public void CanceledPendingAndSubscribedResponsesExposeExpectedStatus()
    {
        var cancellation = new OperationCanceledException("phase-one cancellation");
        var canceled = DurableTaskResponse.FromCanceled(cancellation);

        Assert.Equal(DurableTaskResponseKind.Canceled, canceled.ResponseKind);
        Assert.Equal(DurableTaskStatus.Canceled, canceled.Status);
        Assert.True(canceled.IsCompleted);
        Assert.Null(canceled.ResultType);
        Assert.Same(cancellation, canceled.Exception);
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() => _ = canceled.Result));
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() => canceled.GetResult<int>()));

        foreach (var (response, kind) in new[]
        {
            (DurableTaskResponse.Pending, DurableTaskResponseKind.Pending),
            (DurableTaskResponse.Subscribed, DurableTaskResponseKind.Subscribed),
        })
        {
            Assert.Equal(kind, response.ResponseKind);
            Assert.Equal(DurableTaskStatus.Pending, response.Status);
            Assert.False(response.IsCompleted);
            Assert.Null(response.ResultType);
            Assert.Null(response.Exception);
            var resultException = Assert.Throws<InvalidOperationException>(() => _ = response.Result);
            var typedException = Assert.Throws<InvalidOperationException>(() => response.GetResult<int>());
            Assert.Equal("The durable task has not completed.", resultException.Message);
            Assert.Equal(resultException.Message, typedException.Message);
        }
    }

    [Fact]
    public void PollingOptionsExposeExpectedDefaultsAndAssignedValues()
    {
        var assigned = new PollingOptions { PollTimeout = TimeSpan.FromMilliseconds(275) };
        var zero = new PollingOptions { PollTimeout = TimeSpan.Zero };

        Assert.Equal(TimeSpan.FromSeconds(5), PollingOptions.DefaultPollTimeout);
        Assert.Equal(PollingOptions.DefaultPollTimeout, default(PollingOptions).PollTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(275), assigned.PollTimeout);
        Assert.Equal(TimeSpan.Zero, zero.PollTimeout);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new PollingOptions { PollTimeout = TimeSpan.FromTicks(-1) });
        Assert.Equal("value", exception.ParamName);
    }

    private static InvalidOperationException CreateResponseFailure()
    {
        try
        {
            ThrowResponseFailure();
            throw new InvalidOperationException("The test exception was not thrown.");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowResponseFailure() => throw new InvalidOperationException("phase-one failure");

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

    private static void AssertBuilderNotStarted(Action action)
    {
        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("The durable task builder has not started.", exception.Message);
    }

    private static void AwaitOnCompleted(DurableTaskMethodBuilder builder)
    {
        var awaiter = Task.CompletedTask.GetAwaiter();
        var stateMachine = default(NoopStateMachine);
        builder.AwaitOnCompleted(ref awaiter, ref stateMachine);
    }

    private static void AwaitUnsafeOnCompleted(DurableTaskMethodBuilder builder)
    {
        var awaiter = Task.CompletedTask.GetAwaiter();
        var stateMachine = default(NoopStateMachine);
        builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
    }

    private static void AwaitOnCompleted(DurableTaskMethodBuilder<int> builder)
    {
        var awaiter = Task.CompletedTask.GetAwaiter();
        var stateMachine = default(NoopStateMachine);
        builder.AwaitOnCompleted(ref awaiter, ref stateMachine);
    }

    private static void AwaitUnsafeOnCompleted(DurableTaskMethodBuilder<int> builder)
    {
        var awaiter = Task.CompletedTask.GetAwaiter();
        var stateMachine = default(NoopStateMachine);
        builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
    }

    private struct NoopStateMachine : IAsyncStateMachine
    {
        public void MoveNext() { }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DurableTask Task, WeakReference CapturedState) CreateCompilerLoweredTaskWithCapturedState(bool generic)
    {
        var state = new object();
        var result = new WeakReference(state);
        return (generic ? GenericAsync(state) : NonGenericAsync(state), result);

        static async DurableTask NonGenericAsync(object state)
        {
            await Task.Yield();
            GC.KeepAlive(state);
        }

        static async DurableTask<int> GenericAsync(object state)
        {
            await Task.Yield();
            GC.KeepAlive(state);
            return 42;
        }
    }

    private static void ForceFullCollection()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private readonly struct NonCriticalYieldAwaitable
    {
        public static NonCriticalYieldAwaitable Instance => default;

        public Awaiter GetAwaiter() => default;

        public readonly struct Awaiter : INotifyCompletion
        {
            public bool IsCompleted => false;

            public void GetResult() { }

            public void OnCompleted(Action continuation)
                => ThreadPool.UnsafeQueueUserWorkItem(
                    static (Action callback) => callback(),
                    continuation,
                    preferLocal: false);
        }
    }

    private readonly struct CriticalYieldAwaitable
    {
        public static CriticalYieldAwaitable Instance => default;

        public Awaiter GetAwaiter() => default;

        public readonly struct Awaiter : ICriticalNotifyCompletion
        {
            public bool IsCompleted => false;

            public void GetResult() { }

            public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation)
                => ThreadPool.UnsafeQueueUserWorkItem(
                    static (Action callback) => callback(),
                    continuation,
                    preferLocal: false);
        }
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

    [Fact]
    public async Task RunWithStateInvokesActionWithCapturedState()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("state-action"));
        var state = new RunState("sync-action", 17);
        RunState? observedState = null;
        CancellationToken observedToken = default;
        var invocationCount = 0;
        var definition = DurableTask.Run<RunState>((captured, token) =>
        {
            observedState = captured;
            observedToken = token;
            invocationCount++;
        }, state);

        var response = await DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.Same(state, observedState);
        Assert.Equal(("sync-action", 17), (observedState!.Name, observedState.Value));
        Assert.Equal(context.CancellationToken, observedToken);
        Assert.Equal(1, invocationCount);
        Assert.Same(DurableTaskResponse.Completed, response);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
    }

    [Fact]
    public async Task RunWithStateInvokesAsyncActionWithCapturedState()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("state-async-action"));
        var state = new RunState("async-action", 23);
        RunState? observedState = null;
        CancellationToken observedToken = default;
        var invocationCount = 0;
        var definition = DurableTask.Run<RunState>(async (captured, token) =>
        {
            invocationCount++;
            await Task.Yield();
            observedState = captured;
            observedToken = token;
        }, state);

        var response = await DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.Same(state, observedState);
        Assert.Equal(("async-action", 23), (observedState!.Name, observedState.Value));
        Assert.Equal(context.CancellationToken, observedToken);
        Assert.Equal(1, invocationCount);
        Assert.Same(DurableTaskResponse.Completed, response);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
    }

    [Fact]
    public async Task RunWithStateReturnsFunctionResult()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("state-function"));
        var state = new RunState("sync-function", 29);
        RunState? observedState = null;
        CancellationToken observedToken = default;
        var invocationCount = 0;
        var definition = DurableTask.Run<RunState, int>((captured, token) =>
        {
            observedState = captured;
            observedToken = token;
            invocationCount++;
            return captured.Value * 2;
        }, state);

        var response = await DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.Same(state, observedState);
        Assert.Equal(("sync-function", 29), (observedState!.Name, observedState.Value));
        Assert.Equal(context.CancellationToken, observedToken);
        Assert.Equal(1, invocationCount);
        Assert.Equal(58, response.GetResult<int>());
        Assert.Equal(typeof(int), response.ResultType);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
    }

    [Fact]
    public async Task RunWithStateReturnsAsyncFunctionResult()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("state-async-function"));
        var state = new RunState("async-function", 31);
        RunState? observedState = null;
        CancellationToken observedToken = default;
        var invocationCount = 0;
        var definition = DurableTask.Run<RunState, int>(async (captured, token) =>
        {
            invocationCount++;
            await Task.Yield();
            observedState = captured;
            observedToken = token;
            return captured.Value * 3;
        }, state);

        var response = await DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.Same(state, observedState);
        Assert.Equal(("async-function", 31), (observedState!.Name, observedState.Value));
        Assert.Equal(context.CancellationToken, observedToken);
        Assert.Equal(1, invocationCount);
        Assert.Equal(93, response.GetResult<int>());
        Assert.Equal(typeof(int), response.ResultType);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
    }

    [Fact]
    public async Task RunConvertsSynchronousDelegateExceptionToDurableFailure()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var actionContext = host.CreateContext(TaskId.CreateRoot("sync-action-failure"));
        var functionContext = host.CreateContext(TaskId.CreateRoot("sync-function-failure"));
        var failure = new InvalidOperationException("synchronous phase-two failure");
        var actionCalls = 0;
        var functionCalls = 0;
        var action = DurableTask.Run((Action<CancellationToken>)(_ =>
        {
            actionCalls++;
            throw failure;
        }));
        var function = DurableTask.Run((Func<CancellationToken, int>)(_ =>
        {
            functionCalls++;
            throw failure;
        }));

        var actionResponse = await DurableTaskRuntimeHelper.RunAsync(action, actionContext);
        var functionResponse = await DurableTaskRuntimeHelper.RunAsync(function, functionContext);

        AssertDurableFailureResponse(actionResponse, failure);
        AssertDurableFailureResponse(functionResponse, failure);
        Assert.Equal(1, actionCalls);
        Assert.Equal(1, functionCalls);
        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task RunConvertsAsynchronousDelegateExceptionToDurableFailure()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var actionContext = host.CreateContext(TaskId.CreateRoot("async-action-failure"));
        var functionContext = host.CreateContext(TaskId.CreateRoot("async-function-failure"));
        var failure = new InvalidOperationException("asynchronous phase-two failure");
        var actionCalls = 0;
        var functionCalls = 0;
        var action = DurableTask.Run((Func<CancellationToken, Task>)(_ =>
        {
            actionCalls++;
            return Task.FromException(failure);
        }));
        var function = DurableTask.Run<int>(_ =>
        {
            functionCalls++;
            return Task.FromException<int>(failure);
        });

        var actionResponse = await DurableTaskRuntimeHelper.RunAsync(action, actionContext);
        var functionResponse = await DurableTaskRuntimeHelper.RunAsync(function, functionContext);

        AssertDurableFailureResponse(actionResponse, failure);
        AssertDurableFailureResponse(functionResponse, failure);
        Assert.Equal(1, actionCalls);
        Assert.Equal(1, functionCalls);
        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public void WithIdRejectsAssigningASecondId()
    {
        var invocationCount = 0;
        var configured = DurableTask.Run(_ => invocationCount++).WithId("first-id");

        var exception = Assert.Throws<InvalidOperationException>(() => configured.WithId("second-id"));

        Assert.Equal("The durable task identifier has already been specified.", exception.Message);
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public void WithIdRejectsDefaultTaskId()
    {
        var definition = DurableTask.Run(static _ => { });

        var exception = Assert.Throws<ArgumentException>(
            () => definition.WithId(string.Empty));

        Assert.Equal("segment", exception.ParamName);
        Assert.Equal(
            "The value cannot be an empty string. (Parameter 'segment')",
            exception.Message);
    }

    [Fact]
    public Task DurableTaskAwaiterOnCompletedForwardsContinuation()
        => AssertNonGenericDurableTaskAwaiterForwardsContinuation(unsafeContinuation: false, "safe-await-root");

    [Fact]
    public Task DurableTaskAwaiterUnsafeOnCompletedForwardsContinuation()
        => AssertNonGenericDurableTaskAwaiterForwardsContinuation(unsafeContinuation: true, "unsafe-await-root");

    [Fact]
    public Task GenericDurableTaskAwaiterOnCompletedForwardsContinuationAndResult()
        => AssertGenericDurableTaskAwaiterForwardsContinuation(
            unsafeContinuation: false,
            "generic-safe-await-root",
            expectedResult: 137);

    [Fact]
    public Task GenericDurableTaskAwaiterUnsafeOnCompletedForwardsContinuationAndResult()
        => AssertGenericDurableTaskAwaiterForwardsContinuation(
            unsafeContinuation: true,
            "generic-unsafe-await-root",
            expectedResult: 211);

    private static async Task AssertNonGenericDurableTaskAwaiterForwardsContinuation(
        bool unsafeContinuation,
        string rootId)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot(rootId));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegateCalls = 0;
        var continuationCalls = 0;

        await host.RunWithAmbientAsync(context, async () =>
        {
            var definition = DurableTask.Run(async _ =>
            {
                delegateCalls++;
                delegateStarted.TrySetResult();
                await release.Task;
            });
            var awaiter = definition.GetAwaiter();
            await delegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(awaiter.IsCompleted);

            if (unsafeContinuation)
            {
                awaiter.UnsafeOnCompleted(Continuation);
            }
            else
            {
                awaiter.OnCompleted(Continuation);
            }

            release.TrySetResult();
            await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(awaiter.IsCompleted);
            awaiter.GetResult();
            Assert.Equal(1, continuationCalls);
            Assert.Equal(1, delegateCalls);

            void Continuation()
            {
                Interlocked.Increment(ref continuationCalls);
                continuationRan.TrySetResult();
            }
        });

        var childId = context.TaskId.Child("$child-1");
        var response = await host.GetEntry(childId).WaitAsync(default);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal([childId], host.EntryIds);
        Assert.Equal(1, host.ExecutionCount);
        Assert.Null(DurableExecutionContext.Current);
    }

    private static async Task AssertGenericDurableTaskAwaiterForwardsContinuation(
        bool unsafeContinuation,
        string rootId,
        int expectedResult)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot(rootId));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegateCalls = 0;
        var continuationCalls = 0;

        await host.RunWithAmbientAsync(context, async () =>
        {
            var definition = DurableTask.Run(async _ =>
            {
                delegateCalls++;
                delegateStarted.TrySetResult();
                await release.Task;
                return expectedResult;
            });
            var awaiter = definition.GetAwaiter();
            await delegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(awaiter.IsCompleted);

            if (unsafeContinuation)
            {
                awaiter.UnsafeOnCompleted(Continuation);
            }
            else
            {
                awaiter.OnCompleted(Continuation);
            }

            release.TrySetResult();
            await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(awaiter.IsCompleted);
            Assert.Equal(expectedResult, awaiter.GetResult());
            Assert.Equal(1, continuationCalls);
            Assert.Equal(1, delegateCalls);

            void Continuation()
            {
                Interlocked.Increment(ref continuationCalls);
                continuationRan.TrySetResult();
            }
        });

        var childId = context.TaskId.Child("$child-1");
        var response = await host.GetEntry(childId).WaitAsync(default);
        Assert.Equal(expectedResult, response.GetResult<int>());
        Assert.Equal(typeof(int), response.ResultType);
        Assert.Equal([childId], host.EntryIds);
        Assert.Equal(1, host.ExecutionCount);
        Assert.Null(DurableExecutionContext.Current);
    }

    private static void AssertDurableFailureResponse(
        DurableTaskResponse response,
        InvalidOperationException expected)
    {
        Assert.Equal(DurableTaskResponseKind.Failed, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.True(response.IsCompleted);
        Assert.Same(expected, response.Exception);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => _ = response.Result));
    }

    private sealed record RunState(string Name, int Value);

    [Fact]
    public void DurableTaskExtensionsRejectNullDefinitions()
    {
        DurableTask untyped = null!;
        DurableTask<int> typed = null!;

        var untypedException = Assert.Throws<ArgumentNullException>(() => untyped.WithId("untyped"));
        var typedException = Assert.Throws<ArgumentNullException>(() => typed.WithId("typed"));

        Assert.Equal("task", untypedException.ParamName);
        Assert.Equal("task", typedException.ParamName);
    }

    [Fact]
    public async Task AsyncDurableTaskRepeatedAwaitsReuseEachBuilderContinuationPath()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);

        var safeVoidFirst = new ControlledSafeAwaitable<int>(11);
        var safeVoidSecond = new ControlledSafeAwaitable<int>(13);
        var safeVoidExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitSafeVoidAsync(),
            host.CreateContext(TaskId.CreateRoot("repeated-safe-void")));
        Assert.Equal(1, safeVoidFirst.OnCompletedCount);
        safeVoidFirst.Complete();
        Assert.Equal(1, safeVoidSecond.OnCompletedCount);
        safeVoidSecond.Complete();
        Assert.Same(DurableTaskResponse.Completed, await safeVoidExecution);
        Assert.Equal(1, safeVoidFirst.GetResultCount);
        Assert.Equal(1, safeVoidSecond.GetResultCount);

        var unsafeVoidFirst = new ControlledUnsafeAwaitable<int>(17);
        var unsafeVoidSecond = new ControlledUnsafeAwaitable<int>(19);
        var unsafeVoidExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitUnsafeVoidAsync(),
            host.CreateContext(TaskId.CreateRoot("repeated-unsafe-void")));
        Assert.Equal(1, unsafeVoidFirst.UnsafeOnCompletedCount);
        unsafeVoidFirst.Complete();
        Assert.Equal(1, unsafeVoidSecond.UnsafeOnCompletedCount);
        unsafeVoidSecond.Complete();
        Assert.Same(DurableTaskResponse.Completed, await unsafeVoidExecution);
        Assert.Equal(1, unsafeVoidFirst.GetResultCount);
        Assert.Equal(1, unsafeVoidSecond.GetResultCount);

        var safeResultFirst = new ControlledSafeAwaitable<int>(23);
        var safeResultSecond = new ControlledSafeAwaitable<int>(29);
        var safeResultExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitSafeResultAsync(),
            host.CreateContext(TaskId.CreateRoot("repeated-safe-result")));
        Assert.Equal(1, safeResultFirst.OnCompletedCount);
        safeResultFirst.Complete();
        Assert.Equal(1, safeResultSecond.OnCompletedCount);
        safeResultSecond.Complete();
        var safeResult = await safeResultExecution;
        Assert.Equal(52, safeResult.GetResult<int>());
        Assert.Equal(typeof(int), safeResult.ResultType);
        Assert.Equal(1, safeResultFirst.GetResultCount);
        Assert.Equal(1, safeResultSecond.GetResultCount);

        var unsafeResultFirst = new ControlledUnsafeAwaitable<int>(31);
        var unsafeResultSecond = new ControlledUnsafeAwaitable<int>(37);
        var unsafeResultExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitUnsafeResultAsync(),
            host.CreateContext(TaskId.CreateRoot("repeated-unsafe-result")));
        Assert.Equal(1, unsafeResultFirst.UnsafeOnCompletedCount);
        unsafeResultFirst.Complete();
        Assert.Equal(1, unsafeResultSecond.UnsafeOnCompletedCount);
        unsafeResultSecond.Complete();
        var unsafeResult = await unsafeResultExecution;
        Assert.Equal(68, unsafeResult.GetResult<int>());
        Assert.Equal(typeof(int), unsafeResult.ResultType);
        Assert.Equal(1, unsafeResultFirst.GetResultCount);
        Assert.Equal(1, unsafeResultSecond.GetResultCount);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask AwaitSafeVoidAsync()
        {
            _ = await safeVoidFirst;
            _ = await safeVoidSecond;
        }

        async DurableTask AwaitUnsafeVoidAsync()
        {
            _ = await unsafeVoidFirst;
            _ = await unsafeVoidSecond;
        }

        async DurableTask<int> AwaitSafeResultAsync()
            => await safeResultFirst + await safeResultSecond;

        async DurableTask<int> AwaitUnsafeResultAsync()
            => await unsafeResultFirst + await unsafeResultSecond;
    }

    [Fact]
    public async Task AsyncDurableTaskReferenceAwaitersCompleteEachBuilderContinuationPath()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);

        var safeVoidAwaitable = new ControlledSafeReferenceAwaitable<int>(41);
        var safeVoidExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitSafeVoidAsync(),
            host.CreateContext(TaskId.CreateRoot("reference-safe-void")));
        Assert.Equal(1, safeVoidAwaitable.OnCompletedCount);
        safeVoidAwaitable.Complete();
        Assert.Same(DurableTaskResponse.Completed, await safeVoidExecution);
        Assert.Equal(1, safeVoidAwaitable.GetResultCount);

        var unsafeVoidAwaitable = new ControlledUnsafeReferenceAwaitable<int>(43);
        var unsafeVoidExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitUnsafeVoidAsync(),
            host.CreateContext(TaskId.CreateRoot("reference-unsafe-void")));
        Assert.Equal(1, unsafeVoidAwaitable.UnsafeOnCompletedCount);
        unsafeVoidAwaitable.Complete();
        Assert.Same(DurableTaskResponse.Completed, await unsafeVoidExecution);
        Assert.Equal(1, unsafeVoidAwaitable.GetResultCount);

        var safeResultAwaitable = new ControlledSafeReferenceAwaitable<int>(47);
        var safeResultExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitSafeResultAsync(),
            host.CreateContext(TaskId.CreateRoot("reference-safe-result")));
        Assert.Equal(1, safeResultAwaitable.OnCompletedCount);
        safeResultAwaitable.Complete();
        var safeResult = await safeResultExecution;
        Assert.Equal(48, safeResult.GetResult<int>());
        Assert.Equal(typeof(int), safeResult.ResultType);
        Assert.Equal(1, safeResultAwaitable.GetResultCount);

        var unsafeResultAwaitable = new ControlledUnsafeReferenceAwaitable<int>(53);
        var unsafeResultExecution = DurableTaskRuntimeHelper.RunAsync(
            AwaitUnsafeResultAsync(),
            host.CreateContext(TaskId.CreateRoot("reference-unsafe-result")));
        Assert.Equal(1, unsafeResultAwaitable.UnsafeOnCompletedCount);
        unsafeResultAwaitable.Complete();
        var unsafeResult = await unsafeResultExecution;
        Assert.Equal(54, unsafeResult.GetResult<int>());
        Assert.Equal(typeof(int), unsafeResult.ResultType);
        Assert.Equal(1, unsafeResultAwaitable.GetResultCount);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask AwaitSafeVoidAsync() => _ = await safeVoidAwaitable;
        async DurableTask AwaitUnsafeVoidAsync() => _ = await unsafeVoidAwaitable;
        async DurableTask<int> AwaitSafeResultAsync() => 1 + await safeResultAwaitable;
        async DurableTask<int> AwaitUnsafeResultAsync() => 1 + await unsafeResultAwaitable;
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
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

        var first = await definition.ScheduleAsync("stable-root", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var second = await definition.ScheduleAsync("stable-root", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("stable-root", await first);
        Assert.Equal("stable-root", await second);
        Assert.Equal(1, host.ExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediatelyCompletedScheduledTaskCancellationIsNoOp(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        ScheduledTask scheduled = generic
            ? await host.CreateRootDefinition<int>(
                _ => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(42)))
                .ScheduleAsync($"completed-cancellation-{generic}", cancellationToken: Xunit.TestContext.Current.CancellationToken) : await host.CreateRootDefinition<object?>(
                _ => ValueTask.FromResult(DurableTaskResponse.Completed))
                .ScheduleAsync($"completed-cancellation-{generic}", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await scheduled.CancelAsync(Xunit.TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await scheduled.CancelAsync(cancellation.Token);

        Assert.False(host.IsCancellationRequested(scheduled.Id));
        Assert.True(await scheduled.IsCompletedAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
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
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredTaskRejectsSecondExplicitId(bool generic)
    {
        if (generic)
        {
            var configured = DurableTask.FromResult(42).WithId("first");
            var exception = Assert.Throws<InvalidOperationException>(() => configured.WithId("second"));
            Assert.Equal("The durable task identifier has already been specified.", exception.Message);
        }
        else
        {
            var configured = DurableTask.Run(static _ => { }).WithId("first");
            var exception = Assert.Throws<InvalidOperationException>(() => configured.WithId("second"));
            Assert.Equal("The durable task identifier has already been specified.", exception.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryDurableTaskCannotBeScheduledAsRootEvenWithExplicitId(bool generic)
    {
        if (generic)
        {
            var configured = DurableTask.FromResult(42).WithId("ordinary-generic");
            await Assert.ThrowsAsync<InvalidOperationException>(() => configured.ScheduleAsync(Xunit.TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await configured.CancelAsync(Xunit.TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => configured.PollAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        }
        else
        {
            var configured = DurableTask.Run(static _ => { }).WithId("ordinary");
            await Assert.ThrowsAsync<InvalidOperationException>(() => configured.ScheduleAsync(Xunit.TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await configured.CancelAsync(Xunit.TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => configured.PollAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ExplicitRootSchedulingIgnoresAmbientDurableContext()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var ambient = host.CreateContext(TaskId.CreateRoot("ambient"));
        var definition = host.CreateRootDefinition<string>(
            context => ValueTask.FromResult<DurableTaskResponse>(
                DurableTaskResponse.FromResult(context.TaskId.ToString())));
        ScheduledTask<string>? scheduled = null;

        await host.RunWithAmbientAsync(
            ambient,
            async () => scheduled = await definition.ScheduleAsync("root"));

        Assert.NotNull(scheduled);
        Assert.Equal(TaskId.CreateRoot("root"), scheduled.Id);
        Assert.Equal("root", await scheduled);
        Assert.False(host.Contains(TaskId.Parse("ambient/root")));
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

        var scheduled = await root.ScheduleAsync("root", cancellationToken: Xunit.TestContext.Current.CancellationToken);
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

        var scheduled = await root.ScheduleAsync("snapshot", cancellationToken: Xunit.TestContext.Current.CancellationToken);
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

        var result = await await root.ScheduleAsync("root", cancellationToken: Xunit.TestContext.Current.CancellationToken);

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

        var result = await await root.ScheduleAsync("root", cancellationToken: Xunit.TestContext.Current.CancellationToken);

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
        var winner = await context.SelectForTestAsync(decisionId, candidates, Xunit.TestContext.Current.CancellationToken);
        first.SetResult();
        var replayedWinner = await host.CreateContext(TaskId.CreateRoot("replay"))
            .SelectForTestAsync(decisionId, candidates, Xunit.TestContext.Current.CancellationToken);

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
        var scheduled = await definition.ScheduleAsync("wait", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        using var waitCancellation = new CancellationTokenSource();
        waitCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduled.GetResponseAsync(waitCancellation.Token));
        Assert.False(host.IsCancellationRequested(TaskId.CreateRoot("wait")));

        await scheduled.CancelAsync(Xunit.TestContext.Current.CancellationToken);
        await scheduled.CancelAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.True(host.IsCancellationRequested(TaskId.CreateRoot("wait")));
        completion.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledWhenAllPropagatesFailedAndCanceledResponses(bool canceled)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        Exception expected = canceled
            ? new OperationCanceledException("durable cancellation")
            : new InvalidOperationException("durable failure");
        var nonGenericDefinition = host.CreateRootDefinition<object?>(
            _ => ValueTask.FromResult(DurableTaskResponse.FromException(expected)));
        var genericDefinition = host.CreateRootDefinition<int>(
            _ => ValueTask.FromResult(DurableTaskResponse.FromException(expected)));
        ScheduledTask nonGeneric = await nonGenericDefinition.ScheduleAsync($"all-non-generic-{canceled}", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var generic = await genericDefinition.ScheduleAsync($"all-generic-{canceled}", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        if (canceled)
        {
            var nonGenericException = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ScheduledTask.WhenAll([nonGeneric], Xunit.TestContext.Current.CancellationToken));
            var genericException = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ScheduledTask.WhenAll([generic], Xunit.TestContext.Current.CancellationToken));
            Assert.Same(expected, nonGenericException);
            Assert.Same(expected, genericException);
        }
        else
        {
            var nonGenericException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ScheduledTask.WhenAll([nonGeneric], Xunit.TestContext.Current.CancellationToken));
            var genericException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ScheduledTask.WhenAll([generic], Xunit.TestContext.Current.CancellationToken));
            Assert.Same(expected, nonGenericException);
            Assert.Same(expected, genericException);
        }
    }

    [Fact]
    public async Task ScheduledCombinatorsValidateCollections()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ScheduledTask.WhenAll((IReadOnlyList<ScheduledTask>)null!, Xunit.TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ScheduledTask.WhenAll((IReadOnlyList<ScheduledTask<int>>)null!, Xunit.TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ScheduledTask.WhenAny((IReadOnlyList<ScheduledTask>)null!, Xunit.TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ScheduledTask.WhenAny((IReadOnlyList<ScheduledTask<int>>)null!, Xunit.TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ScheduledTask.WhenAny(Array.Empty<ScheduledTask>(), Xunit.TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ScheduledTask.WhenAny(Array.Empty<ScheduledTask<int>>(), Xunit.TestContext.Current.CancellationToken));

        await ScheduledTask.WhenAll(Array.Empty<ScheduledTask>(), Xunit.TestContext.Current.CancellationToken);
        await ScheduledTask.WhenAll(Array.Empty<ScheduledTask<int>>(), Xunit.TestContext.Current.CancellationToken);
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
        ScheduledTask first = await firstDefinition.ScheduleAsync("first", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await secondDefinition.ScheduleAsync("second", cancellationToken: Xunit.TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task ScheduledWhenAnyWaitsForLosingWaitCancellationToDrain()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var loserCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerDefinition = host.CreateRootDefinition<object?>(
            _ => ValueTask.FromResult(DurableTaskResponse.Completed));
        var loserDefinition = host.CreateRootDefinition<object?>(async _ =>
        {
            await loserCompletion.Task;
            return DurableTaskResponse.Completed;
        });
        ScheduledTask winner = await winnerDefinition.ScheduleAsync("drain-winner", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask loser = await loserDefinition.ScheduleAsync("drain-loser", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var loserEntry = host.GetEntry(loser.Id);
        loserEntry.DelayWaitCancellationCompletion = true;

        var whenAny = ScheduledTask.WhenAny([winner, loser], Xunit.TestContext.Current.CancellationToken);
        await loserEntry.WaitCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.False(whenAny.IsCompleted);
        loserEntry.WaitCancellationRelease.TrySetResult();
        Assert.Same(winner, await whenAny.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(0, host.ActiveWaitCount);
        Assert.False(host.IsCancellationRequested(loser.Id));

        loserCompletion.TrySetResult();
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
        ScheduledTask expectedWinner = await winnerDefinition.ScheduleAsync("winner", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask loser = await loserDefinition.ScheduleAsync("loser", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var winner = await ScheduledTask.WhenAny([expectedWinner, loser], Xunit.TestContext.Current.CancellationToken);
        var response = await winner.GetResponseAsync(Xunit.TestContext.Current.CancellationToken);

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
        ScheduledTask first = await firstDefinition.ScheduleAsync("transport-failure", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await secondDefinition.ScheduleAsync("transport-loser", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("host wait failed");
        host.GetEntry(first.Id).WaitException = expected;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ScheduledTask.WhenAny([first, second], Xunit.TestContext.Current.CancellationToken));

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
        ScheduledTask first = await firstDefinition.ScheduleAsync("started-before-failure", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await secondDefinition.ScheduleAsync("synchronous-wait-failure", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("synchronous host wait failure");
        host.GetEntry(second.Id).WaitException = expected;
        host.GetEntry(second.Id).WaitExceptionIsSynchronous = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ScheduledTask.WhenAny([first, second], Xunit.TestContext.Current.CancellationToken));

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
        var generic = await definition.ScheduleAsync("wait-result", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask nonGeneric = generic;

        await nonGeneric.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        var response = await nonGeneric.GetResponseAsync(Xunit.TestContext.Current.CancellationToken);
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
        var generic = await definition.ScheduleAsync($"terminal-{canceled}", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask nonGeneric = generic;

        var nonGenericException = await Assert.ThrowsAnyAsync<Exception>(
            async () => await nonGeneric.WaitAsync(Xunit.TestContext.Current.CancellationToken));
        var genericException = await Assert.ThrowsAnyAsync<Exception>(
            async () => _ = await generic);

        Assert.Same(expected, nonGenericException);
        Assert.Same(expected, genericException);
        Assert.Equal(
            canceled ? DurableTaskStatus.Canceled : DurableTaskStatus.Failed,
            (await nonGeneric.GetResponseAsync(Xunit.TestContext.Current.CancellationToken)).Status);
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
        var scheduled = await definition.ScheduleAsync("poll", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var status = await scheduled.GetStatusAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken);

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

        var scheduled = await definition.ScheduleAsync("tenant/workflow", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(@"tenant\/workflow", scheduled.Id.ToString());
        Assert.Equal(@"tenant\/workflow", await scheduled);
        Assert.NotEqual(TaskId.Parse("tenant/workflow"), scheduled.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledWhenAnyReturnsSecondWinnerAndDrainsFirstLoser(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var loserCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserDefinition = host.CreateRootDefinition<int>(async _ =>
        {
            await loserCompletion.Task;
            return DurableTaskResponse.FromResult(17);
        });
        var winnerDefinition = host.CreateRootDefinition<int>(
            _ => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(42)));
        var loser = await loserDefinition.ScheduleAsync($"second-winner-loser-{generic}", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expectedWinner = await winnerDefinition.ScheduleAsync($"second-winner-winner-{generic}", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var loserEntry = host.GetEntry(loser.Id);
        loserEntry.DelayWaitCancellationCompletion = true;
        Assert.Equal(42, (await expectedWinner.GetResponseAsync(Xunit.TestContext.Current.CancellationToken)).GetResult<int>());

        Task<ScheduledTask> whenAny = generic
            ? AwaitGenericWinnerAsync([loser, expectedWinner])
            : ScheduledTask.WhenAny([(ScheduledTask)loser, expectedWinner], Xunit.TestContext.Current.CancellationToken);
        await loserEntry.WaitCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.False(whenAny.IsCompleted);
        loserEntry.WaitCancellationRelease.TrySetResult();
        Assert.Same(expectedWinner, await whenAny.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(0, host.ActiveWaitCount);
        Assert.False(host.IsCancellationRequested(loser.Id));
        Assert.False(host.IsCancellationRequested(expectedWinner.Id));

        loserCompletion.TrySetResult();

        static async Task<ScheduledTask> AwaitGenericWinnerAsync(IReadOnlyList<ScheduledTask<int>> tasks)
            => await ScheduledTask.WhenAny(tasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAnyReplayUsesStableOperationDecisionIdThroughPublicApi(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootId = TaskId.CreateRoot("replay");

        var firstWinner = await ExecuteAsync();
        firstCompletion.TrySetResult();
        _ = await host.GetEntry(TaskId.Parse("replay/$when-any-1/0")).WaitAsync(Xunit.TestContext.Current.CancellationToken);
        var replayedWinner = await ExecuteAsync();

        var expectedWinner = TaskId.Parse("replay/$when-any-1/1");
        var expectedDecisionId = TaskId.Parse("replay/$when-any-1/$winner");
        Assert.Equal(expectedWinner, firstWinner);
        Assert.Equal(expectedWinner, replayedWinner);
        Assert.Equal([expectedDecisionId, expectedDecisionId], host.DecisionIds);

        async Task<TaskId> ExecuteAsync()
        {
            DurableTask<TaskId> definition = generic
                ? DurableTask.WhenAny(
                [
                    DurableTask.Run(async _ =>
                    {
                        await firstCompletion.Task;
                        return 17;
                    }),
                    DurableTask.Run(static _ => 42),
                ])
                : DurableTask.WhenAny(
                [
                    DurableTask.Run(async _ => await firstCompletion.Task),
                    DurableTask.Run(static _ => { }),
                ]);
            var response = await DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(rootId));
            return response.GetResult<TaskId>();
        }
    }

    [Fact]
    public async Task IsCompletedAsyncReportsPendingAndTerminalAndForwardsOptions()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = host.CreateRootDefinition<object?>(async _ =>
        {
            await completion.Task;
            return DurableTaskResponse.Completed;
        });
        ScheduledTask scheduled = await definition.ScheduleAsync("completion-state", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var options = new PollingOptions { PollTimeout = TimeSpan.FromMilliseconds(137) };

        Assert.False(await scheduled.IsCompletedAsync(options, Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(options.PollTimeout, host.LastPollTimeout);
        completion.TrySetResult();
        var terminal = await scheduled.GetResponseAsync(Xunit.TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, terminal.Status);
        Assert.True(await scheduled.IsCompletedAsync(options, Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(options.PollTimeout, host.LastPollTimeout);
    }

    [Fact]
    public async Task ScheduledWaitRejectsIncompleteHostResponse()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var definition = host.CreateRootDefinition<object?>(
            _ => ValueTask.FromResult(DurableTaskResponse.Pending));
        ScheduledTask scheduled = await definition.ScheduleAsync("incomplete-wait", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await scheduled.WaitAsync(Xunit.TestContext.Current.CancellationToken));

        Assert.Equal("The durable task has not completed.", exception.Message);
        Assert.Same(DurableTaskResponse.Pending, await scheduled.GetResponseAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(0, host.ActiveWaitCount);
    }

    [Fact]
    public async Task ScheduleAsyncUsesParentHandleWhenContextHasCurrentOperation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var parent = host.CreateContext(TaskId.CreateRoot("parent-handle"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScheduledTask<int>? scheduled = null;

        await host.RunWithAmbientAsync(parent, async () =>
        {
            var configured = DurableTask.Run(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return 73;
            }).WithId("selected-child");
            scheduled = await configured.ScheduleAsync();
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(scheduled);
        var expectedId = TaskId.Parse("parent-handle/selected-child");
        var expectedEntry = host.GetEntry(expectedId);
        Assert.Equal(expectedId, scheduled.Id);
        Assert.Equal([expectedId], host.EntryIds);
        Assert.False(await scheduled.IsCompletedAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        Assert.Same(DurableTaskResponse.Pending, await expectedEntry.PollAsync(default, Xunit.TestContext.Current.CancellationToken));

        release.TrySetResult();
        Assert.Equal(73, await scheduled);
        Assert.Equal(73, (await expectedEntry.WaitAsync(Xunit.TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(1, host.ExecutionCount);
    }

    [Fact]
    public async Task ScheduleAsyncUsesRootHandleWhenContextHasNoCurrentOperation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = host.CreateRootDefinition<int>(async context =>
        {
            Assert.Equal(TaskId.CreateRoot("selected-root"), context.TaskId);
            started.TrySetResult();
            await release.Task;
            return DurableTaskResponse.FromResult(89);
        });

        Assert.Null(DurableExecutionContext.Current);
        var scheduled = await definition.WithId("selected-root").ScheduleAsync(Xunit.TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        var expectedId = TaskId.CreateRoot("selected-root");
        var expectedEntry = host.GetEntry(expectedId);
        Assert.Equal(expectedId, scheduled.Id);
        Assert.Equal([expectedId], host.EntryIds);
        Assert.False(await scheduled.IsCompletedAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        Assert.Same(DurableTaskResponse.Pending, await expectedEntry.PollAsync(default, Xunit.TestContext.Current.CancellationToken));

        release.TrySetResult();
        Assert.Equal(89, await scheduled);
        Assert.Equal(89, (await expectedEntry.WaitAsync(Xunit.TestContext.Current.CancellationToken)).GetResult<int>());
        Assert.Equal(1, host.ExecutionCount);
    }

    [Fact]
    public async Task NonGenericScheduleAsyncWithExplicitRootForwardsRootAndId()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestContext? observedContext = null;
        var invocationCount = 0;
        DurableTask definition = host.CreateRootDefinition<object?>(async context =>
        {
            observedContext = context;
            invocationCount++;
            started.TrySetResult();
            await release.Task;
            return DurableTaskResponse.Completed;
        });

        var scheduled = await definition.ScheduleAsync("phase2/root", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        var expectedId = TaskId.CreateRoot("phase2/root");
        var expectedEntry = host.GetEntry(expectedId);
        Assert.Equal(expectedId, scheduled.Id);
        Assert.Equal(@"phase2\/root", scheduled.Id.ToString());
        Assert.Equal([expectedId], host.EntryIds);
        Assert.Equal(expectedId, observedContext!.TaskId);
        Assert.False(await scheduled.IsCompletedAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        Assert.Same(DurableTaskResponse.Pending, await expectedEntry.PollAsync(default, Xunit.TestContext.Current.CancellationToken));

        release.TrySetResult();
        await scheduled;
        Assert.Same(DurableTaskResponse.Completed, await expectedEntry.WaitAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(1, invocationCount);
        Assert.Equal(1, host.ExecutionCount);
        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task ScheduleAsyncCanceledDuringWaitConstructionCancelsAndRemovesWait()
    {
        var firstState = new ControlledScheduledWaitState();
        var secondState = new ControlledScheduledWaitState();
        ScheduledTask first = await new ControlledRootDefinition<object?>(firstState).ScheduleAsync("construction-cancel-first", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await new ControlledRootDefinition<object?>(secondState).ScheduleAsync("construction-cancel-second", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        secondState.WaitConstruction = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScheduledTask.WhenAny([first, second], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, firstState.WaitCallCount);
        Assert.Equal(1, secondState.WaitCallCount);
        Assert.Equal(0, firstState.ActiveWaitCount);
        Assert.Equal(0, firstState.ActiveRegistrationCount);
        Assert.Equal(0, secondState.ActiveWaitCount);
        Assert.Equal(0, secondState.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ScheduleAsyncFailureDuringWaitConstructionPropagatesAndRemovesWait()
    {
        var firstState = new ControlledScheduledWaitState();
        var secondState = new ControlledScheduledWaitState();
        ScheduledTask first = await new ControlledRootDefinition<object?>(firstState).ScheduleAsync("construction-failure-first", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await new ControlledRootDefinition<object?>(secondState).ScheduleAsync("construction-failure-second", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("wait construction failed");
        secondState.WaitConstruction = _ => throw expected;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ScheduledTask.WhenAny([first, second], Xunit.TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        Assert.Equal(1, firstState.WaitCallCount);
        Assert.Equal(1, secondState.WaitCallCount);
        Assert.Equal(0, firstState.ActiveWaitCount);
        Assert.Equal(0, firstState.ActiveRegistrationCount);
        Assert.Equal(0, secondState.ActiveWaitCount);
        Assert.Equal(0, secondState.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ScheduleAsyncCleanupFailureDoesNotReplacePrimaryCancellation()
    {
        var firstState = new ControlledScheduledWaitState
        {
            CancellationDrainException = new ApplicationException("cleanup failed"),
        };
        var secondState = new ControlledScheduledWaitState();
        ScheduledTask first = await new ControlledRootDefinition<object?>(firstState).ScheduleAsync("canceled-cleanup-first", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await new ControlledRootDefinition<object?>(secondState).ScheduleAsync("canceled-cleanup-second", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        secondState.WaitConstruction = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScheduledTask.WhenAny([first, second], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, firstState.CancellationObservedCount);
        Assert.Equal(0, firstState.ActiveWaitCount);
        Assert.Equal(0, firstState.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ScheduleAsyncCleanupFailureDoesNotReplacePrimaryConstructionFailure()
    {
        var firstState = new ControlledScheduledWaitState
        {
            CancellationDrainException = new ApplicationException("cleanup failed"),
        };
        var secondState = new ControlledScheduledWaitState();
        ScheduledTask first = await new ControlledRootDefinition<object?>(firstState).ScheduleAsync("failed-cleanup-first", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await new ControlledRootDefinition<object?>(secondState).ScheduleAsync("failed-cleanup-second", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("primary construction failure");
        secondState.WaitConstruction = _ => throw expected;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ScheduledTask.WhenAny([first, second], Xunit.TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        Assert.Equal(1, firstState.CancellationObservedCount);
        Assert.Equal(0, firstState.ActiveWaitCount);
        Assert.Equal(0, firstState.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ScheduleAsyncCancellationCallbackFailureDoesNotLeakRegistration()
    {
        var primaryState = new ControlledScheduledWaitState { UseFaultSource = true };
        var losingState = new ControlledScheduledWaitState
        {
            CancellationCallbackException = new ApplicationException("cancellation callback failed"),
        };
        ScheduledTask primary = await new ControlledRootDefinition<object?>(primaryState).ScheduleAsync("callback-primary", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask loser = await new ControlledRootDefinition<object?>(losingState).ScheduleAsync("callback-loser", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("primary wait failed");

        var whenAny = ScheduledTask.WhenAny([primary, loser], Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, primaryState.ActiveWaitCount);
        Assert.Equal(1, losingState.ActiveWaitCount);
        primaryState.Fail(expected);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => whenAny.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        Assert.Equal(1, losingState.CancellationObservedCount);
        Assert.Equal(0, primaryState.ActiveWaitCount);
        Assert.Equal(0, primaryState.ActiveRegistrationCount);
        Assert.Equal(0, losingState.ActiveWaitCount);
        Assert.Equal(0, losingState.ActiveRegistrationCount);
    }

    [Fact]
    public async Task WhenAnyLosingWaitFaultDoesNotOverrideWinner()
    {
        var winningState = new ControlledScheduledWaitState();
        var losingState = new ControlledScheduledWaitState
        {
            UseFaultSource = true,
            IgnoreCancellationWhileWaitingForFault = true,
        };
        ScheduledTask winner = await new ControlledRootDefinition<object?>(winningState).ScheduleAsync("winner", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask loser = await new ControlledRootDefinition<object?>(losingState).ScheduleAsync("loser", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var winningResponse = DurableTaskResponse.Completed;
        var losingException = new InvalidOperationException("losing wait fault");

        var whenAny = ScheduledTask.WhenAny([winner, loser], Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, winningState.ActiveWaitCount);
        Assert.Equal(1, losingState.ActiveWaitCount);
        winningState.Complete(winningResponse);
        await losingState.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.False(whenAny.IsCompleted);
        losingState.Fail(losingException);
        var selected = await whenAny.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.Same(winner, selected);
        Assert.Same(winningResponse, await selected.GetResponseAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(0, winningState.ActiveWaitCount);
        Assert.Equal(0, losingState.ActiveWaitCount);
        Assert.Equal(0, losingState.ActiveRegistrationCount);
    }

    [Fact]
    public async Task TypedConfiguredScheduledTaskAwaiterForwardsContinuationAndResult()
    {
        var state = new ControlledScheduledWaitState();
        var scheduled = await new ControlledRootDefinition<int>(state).ScheduleAsync("typed-configured", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var safeContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsafeContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var safeCalls = 0;
        var unsafeCalls = 0;
        var safeAwaiter = scheduled.WaitAsync(cancellation.Token).GetAwaiter();
        var unsafeAwaiter = scheduled.WaitAsync(cancellation.Token).GetAwaiter();

        Assert.False(safeAwaiter.IsCompleted);
        Assert.False(unsafeAwaiter.IsCompleted);
        Assert.Equal(2, state.ActiveWaitCount);
        Assert.All(state.WaitCancellationTokens, token => Assert.Equal(cancellation.Token, token));
        safeAwaiter.OnCompleted(() =>
        {
            Interlocked.Increment(ref safeCalls);
            safeContinuation.TrySetResult();
        });
        unsafeAwaiter.UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref unsafeCalls);
            unsafeContinuation.TrySetResult();
        });

        state.Complete(DurableTaskResponse.FromResult(73));
        await Task.WhenAll(safeContinuation.Task, unsafeContinuation.Task).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(safeAwaiter.IsCompleted);
        Assert.True(unsafeAwaiter.IsCompleted);
        Assert.Equal(73, ScheduledAwaiterInspector.GetResult(safeAwaiter));
        Assert.Equal(73, ScheduledAwaiterInspector.GetResult(unsafeAwaiter));
        Assert.Equal(1, safeCalls);
        Assert.Equal(1, unsafeCalls);
        await state.WaitsDrained.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(0, state.ActiveWaitCount);
        Assert.Equal(0, state.ActiveRegistrationCount);
    }

    [Fact]
    public async Task NonGenericConfiguredScheduledTaskAwaiterForwardsContinuation()
    {
        var state = new ControlledScheduledWaitState();
        ScheduledTask scheduled = await new ControlledRootDefinition<object?>(state).ScheduleAsync("non-generic-configured", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var safeContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsafeContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var safeCalls = 0;
        var unsafeCalls = 0;
        var safeAwaiter = scheduled.GetAwaiter();
        var unsafeAwaiter = scheduled.GetAwaiter();

        Assert.False(safeAwaiter.IsCompleted);
        Assert.False(unsafeAwaiter.IsCompleted);
        Assert.Equal(2, state.ActiveWaitCount);
        Assert.All(state.WaitCancellationTokens, token => Assert.Equal(CancellationToken.None, token));
        safeAwaiter.OnCompleted(() =>
        {
            Interlocked.Increment(ref safeCalls);
            safeContinuation.TrySetResult();
        });
        unsafeAwaiter.UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref unsafeCalls);
            unsafeContinuation.TrySetResult();
        });

        state.Complete(DurableTaskResponse.Completed);
        await Task.WhenAll(safeContinuation.Task, unsafeContinuation.Task).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(safeAwaiter.IsCompleted);
        Assert.True(unsafeAwaiter.IsCompleted);
        ScheduledAwaiterInspector.GetResult(safeAwaiter);
        ScheduledAwaiterInspector.GetResult(unsafeAwaiter);
        Assert.Equal(1, safeCalls);
        Assert.Equal(1, unsafeCalls);
        await state.WaitsDrained.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(0, state.ActiveWaitCount);
        Assert.Equal(0, state.ActiveRegistrationCount);
    }

    [Fact]
    public async Task ScheduledTaskHandlePropertiesForwardToHostHandle()
    {
        var id = TaskId.CreateRoot("forwarded-handle");
        var handle = new RecordingScheduledTaskHandle(id);
        var definition = new RecordingRootDefinition<int>(handle);
        using var scheduleCancellation = new CancellationTokenSource();
        using var pollCancellation = new CancellationTokenSource();
        using var waitCancellation = new CancellationTokenSource();
        using var cancelCancellation = new CancellationTokenSource();

        var scheduled = await definition.ScheduleAsync(id.ToString(), scheduleCancellation.Token);
        var options = new PollingOptions { PollTimeout = TimeSpan.FromMilliseconds(149) };
        var status = await scheduled.GetStatusAsync(options, pollCancellation.Token);
        var observation = scheduled.GetResponseAsync(waitCancellation.Token);
        await handle.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Assert.False(observation.IsCompleted);
        await scheduled.CancelAsync(cancelCancellation.Token);
        var expected = DurableTaskResponse.FromResult(29);
        handle.Complete(expected);
        var observed = await observation.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(id, scheduled.Id);
        Assert.Equal(DurableTaskStatus.Pending, status);
        Assert.Same(expected, observed);
        Assert.Equal(29, observed.GetResult<int>());
        Assert.Equal(id, definition.ScheduledId);
        Assert.Equal(scheduleCancellation.Token, definition.ScheduleCancellationToken);
        Assert.Equal(1, definition.GetHandleCallCount);
        Assert.Equal(1, handle.PollCallCount);
        Assert.Equal(options.PollTimeout, handle.LastPollingOptions.PollTimeout);
        Assert.Equal(pollCancellation.Token, handle.LastPollCancellationToken);
        Assert.Equal(1, handle.WaitCallCount);
        Assert.Equal(waitCancellation.Token, handle.LastWaitCancellationToken);
        Assert.Equal(1, handle.CancelCallCount);
        Assert.Equal(cancelCancellation.Token, handle.LastCancelCancellationToken);
    }

    [Fact]
    public async Task CompletedScheduledTaskAwaiterCompletesSynchronously()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        DurableTask definition = host.CreateRootDefinition<object?>(
            _ => ValueTask.FromResult(DurableTaskResponse.Completed));
        ScheduledTask scheduled = await definition.ScheduleAsync("completed-await", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationCalls = 0;
        var awaiter = scheduled.GetAwaiter();

        Assert.True(awaiter.IsCompleted);
        awaiter.OnCompleted(() =>
        {
            Interlocked.Increment(ref continuationCalls);
            continuation.TrySetResult();
        });
        await continuation.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        ScheduledAwaiterInspector.GetResult(awaiter);

        Assert.Equal(1, continuationCalls);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await scheduled.GetStatusAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        Assert.Same(DurableTaskResponse.Completed, await scheduled.GetResponseAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.False(host.IsCancellationRequested(scheduled.Id));
    }

    [Fact]
    public async Task GenericCompletedScheduledTaskAwaiterReturnsExactResult()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var expected = new CompletedScheduledResult("completed", 37);
        var expectedResponse = DurableTaskResponse.FromResult(expected);
        var definition = host.CreateRootDefinition<CompletedScheduledResult>(
            _ => ValueTask.FromResult<DurableTaskResponse>(expectedResponse));
        var scheduled = await definition.ScheduleAsync("generic-completed-await", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationCalls = 0;
        var awaiter = scheduled.GetAwaiter();

        Assert.True(awaiter.IsCompleted);
        awaiter.UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref continuationCalls);
            continuation.TrySetResult();
        });
        await continuation.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        var result = ScheduledAwaiterInspector.GetResult(awaiter);

        Assert.Same(expected, result);
        Assert.Equal("completed", result.Name);
        Assert.Equal(37, result.Value);
        Assert.Equal(1, continuationCalls);
        Assert.Same(expectedResponse, await scheduled.GetResponseAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(typeof(CompletedScheduledResult), expectedResponse.ResultType);
    }

    [Fact]
    public async Task CancellationRegistrationDisposeBeforeRequestPreventsCallback()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("dispose-before-request"));
        var callbackCount = 0;
        var tokenNotificationCount = 0;
        using var tokenRegistration = context.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCount));
        var registration = await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref callbackCount);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        var firstDisposal = registration.DisposeAsync();
        var secondDisposal = registration.DisposeAsync();
        Assert.True(firstDisposal.IsCompletedSuccessfully);
        Assert.True(secondDisposal.IsCompletedSuccessfully);
        await firstDisposal;
        await secondDisposal;

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        await cancellation;

        Assert.Equal(0, callbackCount);
        Assert.Equal(1, tokenNotificationCount);
        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationRegistrationDisposeWhileCallbackIsInvokingWaitsForCompletion()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("dispose-while-invoking"));
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var tokenNotificationCount = 0;
        using var tokenRegistration = context.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCount));
        var registration = await context.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCount);
            callbackEntered.SetResult();
            await releaseCallback.Task;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        var disposal = registration.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.False(cancellation.IsCompleted);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, tokenNotificationCount);

        releaseCallback.SetResult();
        await Task.WhenAll(cancellation, disposal).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.True(disposal.IsCompletedSuccessfully);
        Assert.True(registration.DisposeAsync().IsCompletedSuccessfully);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, tokenNotificationCount);
        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationRegistrationCompletionCanRaceDisposal()
    {
        const int AttemptCount = 100;
        var host = new TestHost(DateTimeOffset.UnixEpoch);

        for (var attempt = 0; attempt < AttemptCount; attempt++)
        {
            var context = host.CreateContext(TaskId.CreateRoot($"dispose-completion-race-{attempt}"));
            var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = await context.RegisterCancellationCallbackAsync(async _ =>
            {
                callbackEntered.SetResult();
                await releaseCallback.Task;
            }, Xunit.TestContext.Current.CancellationToken);
            var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
            using var raceGate = new ManualResetEventSlim();
            var disposal = Task.Run(async () =>
            {
                raceGate.Wait();
                await registration.DisposeAsync();
            }, Xunit.TestContext.Current.CancellationToken);
            var release = Task.Run(() =>
            {
                raceGate.Wait();
                releaseCallback.SetResult();
            }, Xunit.TestContext.Current.CancellationToken);

            raceGate.Set();
            await Task.WhenAll(cancellation, disposal, release).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

            Assert.True(cancellation.IsCompletedSuccessfully);
            Assert.True(disposal.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task CancellationRegistrationDisposeAfterCallbackCompletionIsIdempotent()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("dispose-after-completion"));
        var callbackCount = 0;
        var tokenNotificationCount = 0;
        using var tokenRegistration = context.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCount));
        var registration = await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref callbackCount);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        await cancellation;
        var firstDisposal = registration.DisposeAsync();
        var secondDisposal = registration.DisposeAsync();

        Assert.True(firstDisposal.IsCompletedSuccessfully);
        Assert.True(secondDisposal.IsCompletedSuccessfully);
        await firstDisposal;
        await secondDisposal;
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, tokenNotificationCount);
        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.Same(
            cancellation,
            DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken));
        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationRegistrationDisposedFromItsCallbackDoesNotDeadlock()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("self-disposal"));
        var callbackCount = 0;
        var tokenNotificationCount = 0;
        IAsyncDisposable? registration = null;
        using var tokenRegistration = context.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCount));
        registration = await context.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCount);
            var disposal = registration!.DisposeAsync();
            Assert.True(disposal.IsCompletedSuccessfully);
            await disposal;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        await cancellation.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        var repeatedDisposal = registration.DisposeAsync();

        Assert.True(repeatedDisposal.IsCompletedSuccessfully);
        await repeatedDisposal;
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, tokenNotificationCount);
        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationObserverExceptionStillCancelsOperation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("observer-exception"));
        var expected = new InvalidOperationException("token observer failed");
        var tokenObserverCount = 0;
        var durableCallbackCount = 0;
        using var tokenRegistration = context.CancellationToken.Register(() =>
        {
            Interlocked.Increment(ref tokenObserverCount);
            throw expected;
        });
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref durableCallbackCount);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context, Xunit.TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await cancellation);

        Assert.Equal("One or more cancellation observers failed.", exception.Message.Split(" (", 2)[0]);
        Assert.Same(expected, Assert.Single(exception.InnerExceptions));
        Assert.Equal(1, tokenObserverCount);
        Assert.Equal(1, durableCallbackCount);
        Assert.True(cancellation.IsFaulted);
        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationRequestIgnoresCompletedDependencyTarget()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var source = host.CreateContext(TaskId.CreateRoot("completed-target-source"));
        var target = host.CreateContext(TaskId.CreateRoot("completed-target"));
        var sourceCallbackCount = 0;
        var targetCallbackCount = 0;
        var sourceTokenNotificationCount = 0;
        var targetTokenNotificationCount = 0;
        var dependencyRequestCount = 0;
        Task? completedTargetRequest = null;
        using var sourceTokenRegistration = source.CancellationToken.Register(
            () => Interlocked.Increment(ref sourceTokenNotificationCount));
        using var targetTokenRegistration = target.CancellationToken.Register(
            () => Interlocked.Increment(ref targetTokenNotificationCount));
        await target.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref targetCallbackCount);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);
        await source.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref sourceCallbackCount);
            Interlocked.Increment(ref dependencyRequestCount);
            completedTargetRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(target);
            Assert.True(completedTargetRequest.IsCompletedSuccessfully);
            return new(completedTargetRequest);
        }, Xunit.TestContext.Current.CancellationToken);

        var firstTargetRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(target, Xunit.TestContext.Current.CancellationToken);
        await firstTargetRequest;
        var sourceRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(source, Xunit.TestContext.Current.CancellationToken);
        await sourceRequest;

        Assert.Same(firstTargetRequest, completedTargetRequest);
        Assert.Equal(1, dependencyRequestCount);
        Assert.Equal(1, sourceCallbackCount);
        Assert.Equal(1, targetCallbackCount);
        Assert.Equal(1, sourceTokenNotificationCount);
        Assert.Equal(1, targetTokenNotificationCount);
        Assert.True(sourceRequest.IsCompletedSuccessfully);
        Assert.True(firstTargetRequest.IsCompletedSuccessfully);
        Assert.True(source.IsCancellationRequested);
        Assert.True(target.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationGraphVisitsSharedNodeOnce()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateContext(TaskId.CreateRoot("diamond-root"));
        var left = host.CreateContext(TaskId.CreateRoot("diamond-left"));
        var right = host.CreateContext(TaskId.CreateRoot("diamond-right"));
        var shared = host.CreateContext(TaskId.CreateRoot("diamond-shared"));
        var beginBranchDependencies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftRequestedShared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightRequestedShared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCounts = new int[4];
        var tokenNotificationCounts = new int[4];
        var dependencyRequestCounts = new int[4];
        Task? leftSharedRequest = null;
        Task? rightSharedRequest = null;
        using var rootTokenRegistration = root.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[0]));
        using var leftTokenRegistration = left.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[1]));
        using var rightTokenRegistration = right.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[2]));
        using var sharedTokenRegistration = shared.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[3]));
        await shared.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[3]);
            sharedEntered.SetResult();
            await releaseShared.Task;
        }, Xunit.TestContext.Current.CancellationToken);
        await left.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[1]);
            await beginBranchDependencies.Task;
            Interlocked.Increment(ref dependencyRequestCounts[1]);
            leftSharedRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(shared);
            leftRequestedShared.SetResult();
            await leftSharedRequest;
        }, Xunit.TestContext.Current.CancellationToken);
        await right.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[2]);
            await beginBranchDependencies.Task;
            await leftRequestedShared.Task;
            Interlocked.Increment(ref dependencyRequestCounts[2]);
            rightSharedRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(shared);
            rightRequestedShared.SetResult();
            await rightSharedRequest;
        }, Xunit.TestContext.Current.CancellationToken);
        await root.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[0]);
            Interlocked.Add(ref dependencyRequestCounts[0], 2);
            var leftRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(left);
            var rightRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(right);
            await Task.WhenAll(leftRequest, rightRequest);
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(root, Xunit.TestContext.Current.CancellationToken);
        beginBranchDependencies.SetResult();
        await Task.WhenAll(sharedEntered.Task, rightRequestedShared.Task).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.Same(leftSharedRequest, rightSharedRequest);
        Assert.False(cancellation.IsCompleted);
        Assert.Equal([1, 1, 1, 1], callbackCounts);
        Assert.Equal([1, 1, 1, 1], tokenNotificationCounts);
        Assert.Equal([2, 1, 1, 0], dependencyRequestCounts);

        releaseShared.SetResult();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.Equal([1, 1, 1, 1], callbackCounts);
        Assert.Equal([1, 1, 1, 1], tokenNotificationCounts);
        Assert.All(new[] { root, left, right, shared }, context => Assert.True(context.IsCancellationRequested));
    }

    [Fact]
    public async Task CancellationGraphCycleWithSharedTailCancelsEachReachableOperationOnce()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var first = host.CreateContext(TaskId.CreateRoot("cycle-tail-first"));
        var second = host.CreateContext(TaskId.CreateRoot("cycle-tail-second"));
        var third = host.CreateContext(TaskId.CreateRoot("cycle-tail-third"));
        var tail = host.CreateContext(TaskId.CreateRoot("cycle-tail-shared"));
        var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowThirdDependencies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestedTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tailEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRequestedTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCounts = new int[4];
        var tokenNotificationCounts = new int[4];
        var dependencyRequestCounts = new int[4];
        Task? secondTailRequest = null;
        Task? thirdTailRequest = null;
        Task? cycleClosingRequest = null;
        using var firstTokenRegistration = first.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[0]));
        using var secondTokenRegistration = second.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[1]));
        using var thirdTokenRegistration = third.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[2]));
        using var tailTokenRegistration = tail.CancellationToken.Register(
            () => Interlocked.Increment(ref tokenNotificationCounts[3]));
        await tail.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[3]);
            tailEntered.SetResult();
            await releaseTail.Task;
        }, Xunit.TestContext.Current.CancellationToken);
        await third.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[2]);
            thirdEntered.SetResult();
            await allowThirdDependencies.Task;
            Interlocked.Add(ref dependencyRequestCounts[2], 2);
            cycleClosingRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(first);
            Assert.True(cycleClosingRequest.IsCompletedSuccessfully);
            thirdTailRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(tail);
            thirdRequestedTail.SetResult();
            await thirdTailRequest;
        }, Xunit.TestContext.Current.CancellationToken);
        await second.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[1]);
            Interlocked.Add(ref dependencyRequestCounts[1], 2);
            var thirdRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(third);
            await thirdEntered.Task;
            secondTailRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(tail);
            secondRequestedTail.SetResult();
            await Task.WhenAll(thirdRequest, secondTailRequest);
        }, Xunit.TestContext.Current.CancellationToken);
        await first.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[0]);
            Interlocked.Increment(ref dependencyRequestCounts[0]);
            await DurableTaskRuntimeHelper.RequestCancellationAsync(second);
        }, Xunit.TestContext.Current.CancellationToken);

        var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(first, Xunit.TestContext.Current.CancellationToken);
        await Task.WhenAll(secondRequestedTail.Task, tailEntered.Task).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        allowThirdDependencies.SetResult();
        await thirdRequestedTail.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.NotSame(cancellation, cycleClosingRequest);
        Assert.True(cycleClosingRequest!.IsCompletedSuccessfully);
        Assert.Same(secondTailRequest, thirdTailRequest);
        Assert.False(cancellation.IsCompleted);
        Assert.Equal([1, 1, 1, 1], callbackCounts);
        Assert.Equal([1, 1, 1, 1], tokenNotificationCounts);
        Assert.Equal([1, 2, 2, 0], dependencyRequestCounts);

        releaseTail.SetResult();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.Equal([1, 1, 1, 1], callbackCounts);
        Assert.Equal([1, 1, 1, 1], tokenNotificationCounts);
        Assert.All(new[] { first, second, third, tail }, context => Assert.True(context.IsCancellationRequested));
    }

    [Fact]
    public async Task AsyncDurableTaskUsesSafeAwaitContinuation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("safe-continuation"));
        var awaitable = new ControlledSafeAwaitable<int>(41);
        var beforeAwaitCount = 0;
        var afterAwaitCount = 0;
        var definition = ExecuteAsync();

        var execution = DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.False(execution.IsCompleted);
        Assert.Equal(1, beforeAwaitCount);
        Assert.Equal(0, afterAwaitCount);
        Assert.Equal(1, awaitable.OnCompletedCount);
        Assert.Equal(0, awaitable.GetResultCount);

        awaitable.Complete();
        var response = await execution;

        Assert.Equal(DurableTaskResponseKind.CompletedSuccessfully, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(typeof(int), response.ResultType);
        Assert.Equal(1, awaitable.OnCompletedCount);
        Assert.Equal(1, awaitable.GetResultCount);
        Assert.Equal(1, afterAwaitCount);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask<int> ExecuteAsync()
        {
            beforeAwaitCount++;
            var awaited = await awaitable;
            afterAwaitCount++;
            return awaited + 1;
        }
    }

    [Fact]
    public async Task AsyncDurableTaskUsesUnsafeAwaitContinuation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("unsafe-continuation"));
        var awaitable = new ControlledUnsafeAwaitable<string>("unsafe");
        var beforeAwaitCount = 0;
        var afterAwaitCount = 0;
        var definition = ExecuteAsync();

        var execution = DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.False(execution.IsCompleted);
        Assert.Equal(1, beforeAwaitCount);
        Assert.Equal(0, afterAwaitCount);
        Assert.Equal(0, awaitable.OnCompletedCount);
        Assert.Equal(1, awaitable.UnsafeOnCompletedCount);
        Assert.Equal(0, awaitable.GetResultCount);

        awaitable.Complete();
        var response = await execution;

        Assert.Equal(DurableTaskResponseKind.CompletedSuccessfully, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal("unsafe-complete", response.GetResult<string>());
        Assert.Equal(typeof(string), response.ResultType);
        Assert.Equal(0, awaitable.OnCompletedCount);
        Assert.Equal(1, awaitable.UnsafeOnCompletedCount);
        Assert.Equal(1, awaitable.GetResultCount);
        Assert.Equal(1, afterAwaitCount);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask<string> ExecuteAsync()
        {
            beforeAwaitCount++;
            var awaited = await awaitable;
            afterAwaitCount++;
            return $"{awaited}-complete";
        }
    }

    [Fact]
    public async Task AsyncDurableTaskFailureBeforeFirstAwaitProducesFailureResponse()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("failure-before-await"));
        var awaitable = new ControlledSafeAwaitable<int>(17);
        var expected = new InvalidOperationException("failed before first await");
        var bodyCount = 0;
        var definition = ExecuteAsync();

        Assert.Equal(0, bodyCount);
        var response = await DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.Equal(DurableTaskResponseKind.Failed, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Same(expected, response.Exception);
        Assert.Equal(1, bodyCount);
        Assert.Equal(0, awaitable.OnCompletedCount);
        Assert.Equal(0, awaitable.GetResultCount);
        var thrown = Assert.Throws<InvalidOperationException>(() => response.GetResult<object?>());
        Assert.Same(expected, thrown);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask ExecuteAsync()
        {
            bodyCount++;
            if (bodyCount == 1)
            {
                throw expected;
            }

            _ = await awaitable;
        }
    }

    [Fact]
    public async Task AsyncDurableTaskFailureAfterAwaitProducesFailureResponse()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("failure-after-await"));
        var awaitable = new ControlledUnsafeAwaitable<int>(23);
        var expected = new ApplicationException("failed after await");
        var beforeAwaitCount = 0;
        var afterAwaitCount = 0;
        var definition = ExecuteAsync();

        var execution = DurableTaskRuntimeHelper.RunAsync(definition, context);

        Assert.False(execution.IsCompleted);
        Assert.Equal(1, beforeAwaitCount);
        Assert.Equal(0, afterAwaitCount);
        Assert.Equal(1, awaitable.UnsafeOnCompletedCount);
        Assert.Equal(0, awaitable.GetResultCount);

        awaitable.Complete();
        var response = await execution;

        Assert.Equal(DurableTaskResponseKind.Failed, response.ResponseKind);
        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Same(expected, response.Exception);
        Assert.Equal(1, beforeAwaitCount);
        Assert.Equal(1, afterAwaitCount);
        Assert.Equal(0, awaitable.OnCompletedCount);
        Assert.Equal(1, awaitable.UnsafeOnCompletedCount);
        Assert.Equal(1, awaitable.GetResultCount);
        var thrown = Assert.Throws<ApplicationException>(() => response.GetResult<int>());
        Assert.Same(expected, thrown);
        Assert.Null(DurableExecutionContext.Current);

        async DurableTask<int> ExecuteAsync()
        {
            beforeAwaitCount++;
            _ = await awaitable;
            afterAwaitCount++;
            throw expected;
        }
    }

    [Fact]
    public async Task ConfiguredTaskControlOperationsSelectParentAndRootHandles()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var parent = host.CreateContext(TaskId.CreateRoot("control-parent"));
        ConfiguredDurableTask<int> child = default;

        await host.RunWithAmbientAsync(parent, async () =>
        {
            child = DurableTask.FromResult(41).WithId("controlled-child");
            Assert.Equal(DurableTaskStatus.Pending, await child.PollAsync());
            await child.CancelAsync();
        });

        var childId = TaskId.Parse("control-parent/controlled-child");
        Assert.Equal([childId], host.EntryIds);
        Assert.True(host.IsCancellationRequested(childId));

        var rootDefinition = host.CreateRootDefinition<int>(
            _ => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(43)));
        var root = rootDefinition.WithId("controlled-root");

        Assert.Equal(DurableTaskStatus.Pending, await root.PollAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        await root.CancelAsync(Xunit.TestContext.Current.CancellationToken);

        var rootId = TaskId.CreateRoot("controlled-root");
        Assert.Equal([childId, rootId], host.EntryIds);
        Assert.True(host.IsCancellationRequested(rootId));
        Assert.Equal(0, host.ExecutionCount);
        Assert.Null(DurableExecutionContext.Current);
    }

    [Fact]
    public async Task NonGenericScheduledTasksForwardPollingAndCancellationForPendingAndCompletedResponses()
    {
        var pendingHandle = new RecordingScheduledTaskHandle(TaskId.CreateRoot("non-generic-pending"));
        DurableTask pendingDefinition = new NonGenericRootDefinition(
            pendingHandle,
            DurableTaskResponse.Pending);
        using var pollCancellation = new CancellationTokenSource();
        using var cancelCancellation = new CancellationTokenSource();

        var pending = await pendingDefinition.ScheduleAsync("non-generic-pending", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var options = new PollingOptions { PollTimeout = TimeSpan.FromMilliseconds(173) };
        var pendingResponse = await pending.GetResponseAsync(options, pollCancellation.Token);
        await pending.CancelAsync(cancelCancellation.Token);

        Assert.Same(DurableTaskResponse.Pending, pendingResponse);
        Assert.Equal(1, pendingHandle.PollCallCount);
        Assert.Equal(options.PollTimeout, pendingHandle.LastPollingOptions.PollTimeout);
        Assert.Equal(pollCancellation.Token, pendingHandle.LastPollCancellationToken);
        Assert.Equal(1, pendingHandle.CancelCallCount);
        Assert.Equal(cancelCancellation.Token, pendingHandle.LastCancelCancellationToken);

        var completedHandle = new RecordingScheduledTaskHandle(TaskId.CreateRoot("non-generic-completed"));
        DurableTask completedDefinition = new NonGenericRootDefinition(
            completedHandle,
            DurableTaskResponse.Completed);

        var completed = await completedDefinition.ScheduleAsync("non-generic-completed", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await completed.CancelAsync(new CancellationToken(canceled: true));
        var completedResponse = await completed.GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.FromMilliseconds(181) }, Xunit.TestContext.Current.CancellationToken);

        Assert.Same(DurableTaskResponse.Completed, completedResponse);
        Assert.True(await completed.IsCompletedAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken));
        Assert.Equal(0, completedHandle.PollCallCount);
        Assert.Equal(0, completedHandle.CancelCallCount);
    }

    [Fact]
    public async Task ScheduledWhenAnyWaitsForCancellationIgnoringSuccessfulLoser()
    {
        var winnerState = new DirectScheduledTaskState();
        var loserState = new DirectScheduledTaskState();
        winnerState.Complete(DurableTaskResponse.Completed);
        DurableTask winnerDefinition = new DirectRootDefinition(winnerState);
        DurableTask loserDefinition = new DirectRootDefinition(loserState);
        ScheduledTask winner = await winnerDefinition.ScheduleAsync("direct-winner", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask loser = await loserDefinition.ScheduleAsync("direct-loser", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var whenAny = ScheduledTask.WhenAny([winner, loser], Xunit.TestContext.Current.CancellationToken);
        await loserState.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.False(whenAny.IsCompleted);
        Assert.Equal(1, loserState.WaitCallCount);
        Assert.True(loserState.LastCancellationToken.CanBeCanceled);

        loserState.Complete(DurableTaskResponse.Completed);
        var selected = await whenAny.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.Same(winner, selected);
        Assert.Equal(1, winnerState.WaitCallCount);
        Assert.Equal(1, loserState.WaitCallCount);
        Assert.True(loserState.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(0, winnerState.CancelCallCount);
        Assert.Equal(0, loserState.CancelCallCount);
    }

    [Fact]
    public async Task CancellationRequestFromEscapedCompletedCallbackRunsIndependently()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var source = host.CreateContext(TaskId.CreateRoot("escaped-source"));
        var target = host.CreateContext(TaskId.CreateRoot("escaped-target"));
        var releaseEscapedWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var escapedRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var escapedWorkCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceCallbackCount = 0;
        var targetCallbackCount = 0;
        Task? escapedTargetRequest = null;
        await target.RegisterCancellationCallbackAsync(_ =>
        {
            Interlocked.Increment(ref targetCallbackCount);
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);
        await source.RegisterCancellationCallbackAsync(ignored =>
        {
            Interlocked.Increment(ref sourceCallbackCount);
            _ = Task.Run(async () =>
            {
                await releaseEscapedWork.Task;
                escapedTargetRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(target);
                escapedRequestStarted.TrySetResult();
                await escapedTargetRequest;
                escapedWorkCompleted.TrySetResult();
            });
            return ValueTask.CompletedTask;
        }, Xunit.TestContext.Current.CancellationToken);

        var sourceRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(source, Xunit.TestContext.Current.CancellationToken);
        await sourceRequest;
        Assert.True(sourceRequest.IsCompletedSuccessfully);
        Assert.False(target.IsCancellationRequested);

        releaseEscapedWork.TrySetResult();
        await escapedRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        await escapedWorkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(escapedTargetRequest);
        Assert.True(escapedTargetRequest.IsCompletedSuccessfully);
        Assert.Equal(1, sourceCallbackCount);
        Assert.Equal(1, targetCallbackCount);
        Assert.True(source.IsCancellationRequested);
        Assert.True(target.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationGraphTraversalHandlesSharedDependencyWithoutDuplicateCancellation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateContext(TaskId.CreateRoot("traversal-root"));
        var left = host.CreateContext(TaskId.CreateRoot("traversal-left"));
        var right = host.CreateContext(TaskId.CreateRoot("traversal-right"));
        var shared = host.CreateContext(TaskId.CreateRoot("traversal-shared"));
        var outside = host.CreateContext(TaskId.CreateRoot("traversal-outside"));
        var releaseShared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftLinked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightLinked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outsideLinked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCounts = new int[5];
        var uncancelableToken = new CancellationToken(canceled: false);
        Task? outsideRootRequest = null;

        await shared.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[3]);
            sharedEntered.TrySetResult();
            await releaseShared.Task;
        }, Xunit.TestContext.Current.CancellationToken);
        await left.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[1]);
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(shared, uncancelableToken);
            leftLinked.TrySetResult();
            await request;
        }, Xunit.TestContext.Current.CancellationToken);
        await right.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[2]);
            await leftLinked.Task;
            var request = DurableTaskRuntimeHelper.RequestCancellationAsync(shared, uncancelableToken);
            rightLinked.TrySetResult();
            await request;
        }, Xunit.TestContext.Current.CancellationToken);
        await root.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[0]);
            var leftRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(left, uncancelableToken);
            var rightRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(right, uncancelableToken);
            await Task.WhenAll(leftRequest, rightRequest);
        }, Xunit.TestContext.Current.CancellationToken);
        await outside.RegisterCancellationCallbackAsync(async _ =>
        {
            Interlocked.Increment(ref callbackCounts[4]);
            outsideRootRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(root, uncancelableToken);
            outsideLinked.TrySetResult();
            await outsideRootRequest;
        }, Xunit.TestContext.Current.CancellationToken);

        var rootRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(root, uncancelableToken);
        await Task.WhenAll(sharedEntered.Task, rightLinked.Task).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        var outsideRequest = DurableTaskRuntimeHelper.RequestCancellationAsync(outside, uncancelableToken);
        await outsideLinked.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.Same(rootRequest, outsideRootRequest);
        Assert.False(rootRequest.IsCompleted);
        Assert.False(outsideRequest.IsCompleted);
        Assert.Equal([1, 1, 1, 1, 1], callbackCounts);

        releaseShared.TrySetResult();
        await Task.WhenAll(rootRequest, outsideRequest).WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        Assert.True(rootRequest.IsCompletedSuccessfully);
        Assert.True(outsideRequest.IsCompletedSuccessfully);
        Assert.Equal([1, 1, 1, 1, 1], callbackCounts);
        Assert.All(
            new[] { root, left, right, shared, outside },
            context => Assert.True(context.IsCancellationRequested));
    }

    [Fact]
    public async Task ScheduledWhenAnyPropagatesFirstWaitConstructionFailureWithoutStartingLaterWaits()
    {
        var firstState = new ControlledScheduledWaitState();
        var secondState = new ControlledScheduledWaitState();
        ScheduledTask first = await new ControlledRootDefinition<object?>(firstState)
            .ScheduleAsync("first-construction-failure", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        ScheduledTask second = await new ControlledRootDefinition<object?>(secondState)
            .ScheduleAsync("not-started-after-failure", cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("first wait construction failed");
        firstState.WaitConstruction = _ => throw expected;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ScheduledTask.WhenAny([first, second], Xunit.TestContext.Current.CancellationToken));

        Assert.Same(expected, exception);
        Assert.Equal(1, firstState.WaitCallCount);
        Assert.Equal(0, secondState.WaitCallCount);
        Assert.Equal(0, firstState.ActiveWaitCount);
        Assert.Equal(0, firstState.ActiveRegistrationCount);
        Assert.Equal(0, secondState.ActiveWaitCount);
        Assert.Equal(0, secondState.ActiveRegistrationCount);
    }

    [Theory]
    [InlineData(DurableTaskResponseKind.CompletedSuccessfully)]
    [InlineData(DurableTaskResponseKind.Canceled)]
    [InlineData(DurableTaskResponseKind.Failed)]
    public async Task ScheduledWhenAnySingleTerminalTaskReturnsOnlyCandidate(
        DurableTaskResponseKind responseKind)
    {
        var state = new DirectScheduledTaskState();
        DurableTaskResponse response = responseKind switch
        {
            DurableTaskResponseKind.CompletedSuccessfully => DurableTaskResponse.Completed,
            DurableTaskResponseKind.Canceled => DurableTaskResponse.Canceled,
            DurableTaskResponseKind.Failed => DurableTaskResponse.FromException(
                new InvalidOperationException("single terminal failure")),
            _ => throw new InvalidOperationException($"Unsupported test response kind '{responseKind}'."),
        };
        state.Complete(response);
        DurableTask definition = new DirectRootDefinition(state);
        ScheduledTask candidate = await definition.ScheduleAsync($"single-{responseKind}", cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var selected = await ScheduledTask.WhenAny([candidate], Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, state.WaitCallCount);
        Assert.True(state.LastCancellationToken.IsCancellationRequested);
        var selectedResponse = await selected.GetResponseAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Same(candidate, selected);
        Assert.Same(response, selectedResponse);
        Assert.Equal(responseKind, selectedResponse.ResponseKind);
        Assert.Equal(2, state.WaitCallCount);
        Assert.Equal(Xunit.TestContext.Current.CancellationToken, state.LastCancellationToken);
        Assert.Equal(0, state.CancelCallCount);
    }

}

internal sealed class TestHost(DateTimeOffset utcNow)
{
    private readonly ConcurrentDictionary<TaskId, Entry> _entries = new();
    private readonly ConcurrentDictionary<TaskId, TaskId> _decisions = new();
    private readonly ConcurrentQueue<TaskId> _decisionIds = new();
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

    public IReadOnlyList<TaskId> DecisionIds => _decisionIds.ToArray();

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
        _decisionIds.Enqueue(decisionId);
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

        public bool DelayWaitCancellationCompletion { get; set; }

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitCancellationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            catch (OperationCanceledException) when (DelayWaitCancellationCompletion && cancellationToken.IsCancellationRequested)
            {
                WaitCancellationObserved.TrySetResult();
                await WaitCancellationRelease.Task;
                throw;
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

internal sealed class ControlledScheduledWaitState
{
    private readonly TaskCompletionSource<DurableTaskResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<CancellationToken> _waitCancellationTokens = new();
    private int _activeWaitCount;
    private int _activeRegistrationCount;
    private int _waitCallCount;

    public Action<CancellationToken>? WaitConstruction { get; set; }

    public Exception? CancellationDrainException { get; init; }

    public Exception? CancellationCallbackException { get; init; }

    public bool UseFaultSource { get; init; }

    public bool IgnoreCancellationWhileWaitingForFault { get; init; }

    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource WaitsDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CancellationObservedCount { get; private set; }

    public int ActiveWaitCount => Volatile.Read(ref _activeWaitCount);

    public int ActiveRegistrationCount => Volatile.Read(ref _activeRegistrationCount);

    public int WaitCallCount => Volatile.Read(ref _waitCallCount);

    public IReadOnlyList<CancellationToken> WaitCancellationTokens => _waitCancellationTokens.ToArray();

    public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waitCallCount);
        _waitCancellationTokens.Enqueue(cancellationToken);
        WaitConstruction?.Invoke(cancellationToken);
        return WaitAsyncCore(cancellationToken);
    }

    public void Complete(DurableTaskResponse response) => _completion.TrySetResult(response);

    public void Fail(Exception exception) => _failure.TrySetResult(exception);

    private async ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeWaitCount);
        CancellationTokenRegistration registration = default;
        var registrationTracked = false;
        try
        {
            Task<DurableTaskResponse>? completionWait = null;
            Task<Exception>? failureWait = null;
            if (UseFaultSource)
            {
                failureWait = IgnoreCancellationWhileWaitingForFault
                    ? _failure.Task
                    : _failure.Task.WaitAsync(cancellationToken);
            }
            else
            {
                completionWait = _completion.Task.WaitAsync(cancellationToken);
            }

            if (cancellationToken.CanBeCanceled)
            {
                Interlocked.Increment(ref _activeRegistrationCount);
                registrationTracked = true;
                try
                {
                    registration = cancellationToken.Register(() =>
                    {
                        CancellationObservedCount++;
                        CancellationObserved.TrySetResult();
                        if (CancellationCallbackException is { } exception)
                        {
                            throw exception;
                        }
                    });
                }
                catch
                {
                    Interlocked.Decrement(ref _activeRegistrationCount);
                    registrationTracked = false;
                    throw;
                }
            }

            if (UseFaultSource)
            {
                var exception = await failureWait!.ConfigureAwait(false);
                throw exception;
            }

            return await completionWait!.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            && CancellationDrainException is { })
        {
            throw CancellationDrainException;
        }
        finally
        {
            registration.Dispose();
            if (registrationTracked)
            {
                Interlocked.Decrement(ref _activeRegistrationCount);
            }

            if (Interlocked.Decrement(ref _activeWaitCount) == 0)
            {
                WaitsDrained.TrySetResult();
            }
        }
    }
}

internal sealed class ControlledRootDefinition<TResult>(ControlledScheduledWaitState state) : DurableTask<TResult>, ISchedulableTask
{
    private TaskId _taskId;

    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _taskId = taskId;
        return new(DurableTaskResponse.Pending);
    }

    public IScheduledTaskHandle GetHandle(TaskId taskId)
    {
        Assert.Equal(_taskId, taskId);
        return new ControlledScheduledTaskHandle(taskId, state);
    }

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => throw new InvalidOperationException("The controlled root definition is scheduled by its host.");
}

internal sealed class ControlledScheduledTaskHandle(TaskId id, ControlledScheduledWaitState state) : IScheduledTaskHandle
{
    public TaskId TaskId => id;

    public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        => state.WaitAsync(cancellationToken);

    public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        => new(DurableTaskResponse.Pending);

    public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class RecordingRootDefinition<TResult>(RecordingScheduledTaskHandle handle) : DurableTask<TResult>, ISchedulableTask
{
    public TaskId ScheduledId { get; private set; }

    public CancellationToken ScheduleCancellationToken { get; private set; }

    public int GetHandleCallCount { get; private set; }

    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ScheduledId = taskId;
        ScheduleCancellationToken = cancellationToken;
        return new(DurableTaskResponse.Pending);
    }

    public IScheduledTaskHandle GetHandle(TaskId taskId)
    {
        Assert.Equal(handle.TaskId, taskId);
        GetHandleCallCount++;
        return handle;
    }

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => throw new InvalidOperationException("The recording root definition is scheduled by its host.");
}

internal sealed class RecordingScheduledTaskHandle(TaskId id) : IScheduledTaskHandle
{
    private readonly TaskCompletionSource<DurableTaskResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskId TaskId => id;

    public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int WaitCallCount { get; private set; }

    public int PollCallCount { get; private set; }

    public int CancelCallCount { get; private set; }

    public CancellationToken LastWaitCancellationToken { get; private set; }

    public CancellationToken LastPollCancellationToken { get; private set; }

    public CancellationToken LastCancelCancellationToken { get; private set; }

    public PollingOptions LastPollingOptions { get; private set; }

    public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
    {
        WaitCallCount++;
        LastWaitCancellationToken = cancellationToken;
        WaitStarted.TrySetResult();
        return await _completion.Task.WaitAsync(cancellationToken);
    }

    public ValueTask<DurableTaskResponse> PollAsync(
        PollingOptions options,
        CancellationToken cancellationToken)
    {
        PollCallCount++;
        LastPollingOptions = options;
        LastPollCancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return new(DurableTaskResponse.Pending);
    }

    public ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        CancelCallCount++;
        LastCancelCancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public void Complete(DurableTaskResponse response) => _completion.TrySetResult(response);
}

internal sealed record CompletedScheduledResult(string Name, int Value);

internal static class ScheduledAwaiterInspector
{
    public static void GetResult(ScheduledTaskAwaiter awaiter) => awaiter.GetResult();

    public static TResult GetResult<TResult>(ScheduledTaskAwaiter<TResult> awaiter) => awaiter.GetResult();
}

internal sealed class ControlledSafeAwaitable<TResult>(TResult result)
{
    private Action? _continuation;
    private bool _isCompleted;
    private readonly TResult _result = result;

    public int OnCompletedCount { get; private set; }

    public int GetResultCount { get; private set; }

    public Awaiter GetAwaiter() => new(this);

    public void Complete()
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException("The awaitable has already completed.");
        }

        _isCompleted = true;
        (_continuation ?? throw new InvalidOperationException("No continuation was registered."))();
    }

    public readonly struct Awaiter(ControlledSafeAwaitable<TResult> owner) : INotifyCompletion
    {
        public bool IsCompleted => owner._isCompleted;

        public void OnCompleted(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            owner.OnCompletedCount++;
            if (owner._continuation is not null)
            {
                throw new InvalidOperationException("A continuation was already registered.");
            }

            owner._continuation = continuation;
        }

        public TResult GetResult()
        {
            if (!owner._isCompleted)
            {
                throw new InvalidOperationException("The awaitable has not completed.");
            }

            owner.GetResultCount++;
            return owner._result;
        }
    }
}

internal sealed class ControlledUnsafeAwaitable<TResult>(TResult result)
{
    private Action? _continuation;
    private bool _isCompleted;
    private readonly TResult _result = result;

    public int OnCompletedCount { get; private set; }

    public int UnsafeOnCompletedCount { get; private set; }

    public int GetResultCount { get; private set; }

    public Awaiter GetAwaiter() => new(this);

    public void Complete()
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException("The awaitable has already completed.");
        }

        _isCompleted = true;
        (_continuation ?? throw new InvalidOperationException("No continuation was registered."))();
    }

    public readonly struct Awaiter(ControlledUnsafeAwaitable<TResult> owner) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => owner._isCompleted;

        public void OnCompleted(Action continuation)
            => owner.RegisterContinuation(continuation, isUnsafe: false);

        public void UnsafeOnCompleted(Action continuation)
            => owner.RegisterContinuation(continuation, isUnsafe: true);

        public TResult GetResult()
        {
            if (!owner._isCompleted)
            {
                throw new InvalidOperationException("The awaitable has not completed.");
            }

            owner.GetResultCount++;
            return owner._result;
        }
    }

    private void RegisterContinuation(Action continuation, bool isUnsafe)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (_continuation is not null)
        {
            throw new InvalidOperationException("A continuation was already registered.");
        }

        if (isUnsafe)
        {
            UnsafeOnCompletedCount++;
        }
        else
        {
            OnCompletedCount++;
        }

        _continuation = continuation;
    }
}

internal sealed class NonGenericRootDefinition(
    RecordingScheduledTaskHandle handle,
    DurableTaskResponse scheduleResponse) : DurableTask, ISchedulableTask
{
    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(handle.TaskId, taskId);
        return new(scheduleResponse);
    }

    public IScheduledTaskHandle GetHandle(TaskId taskId)
    {
        Assert.Equal(handle.TaskId, taskId);
        return handle;
    }

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => throw new InvalidOperationException("The non-generic root definition is scheduled by its host.");
}

internal sealed class DirectScheduledTaskState
{
    private readonly TaskCompletionSource<DurableTaskResponse> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource WaitStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int WaitCallCount { get; private set; }

    public int CancelCallCount { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
    {
        WaitCallCount++;
        LastCancellationToken = cancellationToken;
        WaitStarted.TrySetResult();
        return new(_completion.Task);
    }

    public ValueTask CancelAsync()
    {
        CancelCallCount++;
        return ValueTask.CompletedTask;
    }

    public void Complete(DurableTaskResponse response) => _completion.TrySetResult(response);
}

internal sealed class DirectRootDefinition(DirectScheduledTaskState state) : DurableTask, ISchedulableTask
{
    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
        => new(DurableTaskResponse.Pending);

    public IScheduledTaskHandle GetHandle(TaskId taskId) => new DirectScheduledTaskHandle(taskId, state);

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => throw new InvalidOperationException("The direct root definition is scheduled by its host.");
}

internal sealed class DirectScheduledTaskHandle(TaskId id, DirectScheduledTaskState state) : IScheduledTaskHandle
{
    public TaskId TaskId => id;

    public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        => state.WaitAsync(cancellationToken);

    public ValueTask<DurableTaskResponse> PollAsync(
        PollingOptions options,
        CancellationToken cancellationToken)
        => new(DurableTaskResponse.Pending);

    public ValueTask CancelAsync(CancellationToken cancellationToken) => state.CancelAsync();
}

internal sealed class ControlledSafeReferenceAwaitable<TResult>(TResult result)
{
    private readonly Awaiter _awaiter = new(result);

    public int OnCompletedCount => _awaiter.OnCompletedCount;

    public int GetResultCount => _awaiter.GetResultCount;

    public Awaiter GetAwaiter() => _awaiter;

    public void Complete() => _awaiter.Complete();

    internal sealed class Awaiter(TResult result) : INotifyCompletion
    {
        private Action? _continuation;
        private bool _isCompleted;

        public int OnCompletedCount { get; private set; }

        public int GetResultCount { get; private set; }

        public bool IsCompleted => _isCompleted;

        public void OnCompleted(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            OnCompletedCount++;
            _continuation = continuation;
        }

        public TResult GetResult()
        {
            Assert.True(_isCompleted);
            GetResultCount++;
            return result;
        }

        public void Complete()
        {
            Assert.False(_isCompleted);
            _isCompleted = true;
            Assert.NotNull(_continuation);
            _continuation();
        }
    }
}

internal sealed class ControlledUnsafeReferenceAwaitable<TResult>(TResult result)
{
    private readonly Awaiter _awaiter = new(result);

    public int UnsafeOnCompletedCount => _awaiter.UnsafeOnCompletedCount;

    public int GetResultCount => _awaiter.GetResultCount;

    public Awaiter GetAwaiter() => _awaiter;

    public void Complete() => _awaiter.Complete();

    internal sealed class Awaiter(TResult result) : ICriticalNotifyCompletion
    {
        private Action? _continuation;
        private bool _isCompleted;

        public int UnsafeOnCompletedCount { get; private set; }

        public int GetResultCount { get; private set; }

        public bool IsCompleted => _isCompleted;

        public void OnCompleted(Action continuation)
            => throw new InvalidOperationException("The compiler should use the unsafe continuation path.");

        public void UnsafeOnCompleted(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            UnsafeOnCompletedCount++;
            _continuation = continuation;
        }

        public TResult GetResult()
        {
            Assert.True(_isCompleted);
            GetResultCount++;
            return result;
        }

        public void Complete()
        {
            Assert.False(_isCompleted);
            _isCompleted = true;
            Assert.NotNull(_continuation);
            _continuation();
        }
    }
}
