using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Tests the distinction between durable task cancellation, which the task observes and records, and the cancellation of
/// an attempt, after which the attempt's durable operations throw <see cref="DurableTaskAttemptCanceledException"/>.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskAttemptCancellationTests
{
    private static readonly TaskId RootId = TaskId.CreateRoot("root");
    private static readonly TaskId ChildId = TaskId.Parse("root/child");
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    public static TheoryData<string, bool> DurableOperations()
    {
        var data = new TheoryData<string, bool>();
        foreach (var operation in new[]
        {
            "RunInline", "RunInlineAction", "AwaitChild", "AwaitNamedChild", "ScheduleChild", "PostChild", "ShieldedChild",
            "DetachedChild", "SignalWait", "SignalTryWait", "Delay", "DelayUntil", "WhenAll", "WhenAny", "WhenEach",
            "WhenAllCompleted", "ScheduleAll", "Race", "WaitAsyncTimeout", "WaitUntilAsync", "ConfiguredCancel",
            "ConfiguredPoll",
        })
        {
            data.Add(operation, false);
            data.Add(operation, true);
        }

        return data;
    }

    public static TheoryData<string> HandleOperations() =>
    [
        "Await", "WaitAsync", "GetResponse", "GetStatus", "IsCompleted", "Cancel", "CancelWithReason", "Abort",
        "WaitAsyncTimeout", "WhenAll", "WhenAny", "WhenEach", "Race",
    ];

    [Fact]
    public void TaskCancellationCancelsOnlyTheCancellationToken()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(RootId);

        DurableTaskRuntimeHelper.RequestTaskCancellation(context, "buyer");

        Assert.True(context.IsTaskCancellationRequested);
        Assert.Equal("buyer", context.TaskCancellationReason);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.False(context.AttemptCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void AttemptCancellationCancelsBothTokensWithoutRequestingTaskCancellation()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(RootId);

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);

        Assert.True(context.AttemptCancellationToken.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.False(context.IsTaskCancellationRequested);
        Assert.Null(context.TaskCancellationReason);
    }

    [Fact]
    public async Task FirstAttemptCancellationDeterminesTheReason()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(RootId);

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskAborted);
        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskDeleted);
        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.Zero), context);
        AssertAttemptCanceled(context, response, DurableTaskAttemptCancellationReason.TaskAborted);
        Assert.Null(host.LastDelayDueTime);
    }

    [Fact]
    public void TaskAndAttemptCancellationsAreIndependentAndCancelTheTokenOnce()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(RootId);
        var tokenObservers = 0;
        var attemptObservers = 0;
        using var tokenRegistration = context.CancellationToken.Register(() => tokenObservers++);
        using var attemptRegistration = context.AttemptCancellationToken.Register(() => attemptObservers++);

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);
        DurableTaskRuntimeHelper.RequestTaskCancellation(context, "buyer");
        DurableTaskRuntimeHelper.RequestTaskCancellation(context, "operator");

        Assert.True(context.IsTaskCancellationRequested);
        Assert.Equal("buyer", context.TaskCancellationReason);
        Assert.Equal(1, tokenObservers);
        Assert.Equal(1, attemptObservers);
    }

    [Fact]
    public void AttemptCancellationAfterTaskCancellationCancelsOnlyTheAttemptToken()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(RootId);
        var tokenObservers = 0;
        var attemptObservers = 0;
        using var tokenRegistration = context.CancellationToken.Register(() => tokenObservers++);
        using var attemptRegistration = context.AttemptCancellationToken.Register(() => attemptObservers++);

        DurableTaskRuntimeHelper.RequestTaskCancellation(context, "buyer");
        Assert.Equal((1, 0), (tokenObservers, attemptObservers));
        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskDeleted);

        Assert.Equal((1, 1), (tokenObservers, attemptObservers));
        Assert.True(context.IsTaskCancellationRequested);
        Assert.Equal("buyer", context.TaskCancellationReason);
    }

    [Fact]
    public void AttemptTokenIsCanceledBeforeCancellationTokenObserversRun()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(RootId);
        (bool AttemptCanceled, bool TaskCancellationRequested)? observed = null;
        bool? cancellationTokenSeenByAttemptObserver = null;
        using var tokenRegistration = context.CancellationToken.Register(
            () => observed = (context.AttemptCancellationToken.IsCancellationRequested, context.IsTaskCancellationRequested));
        using var attemptRegistration = context.AttemptCancellationToken.Register(
            () => cancellationTokenSeenByAttemptObserver = context.CancellationToken.IsCancellationRequested);

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);

        Assert.Equal((true, false), observed);
        Assert.False(cancellationTokenSeenByAttemptObserver);
    }

    [Fact]
    public async Task DurableOperationsRunAfterTaskCancellationSoCompensationCanRecordItsWork()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(RootId);
        DurableTaskRuntimeHelper.RequestTaskCancellation(context, "buyer");

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                Assert.True(cancellationToken.IsCancellationRequested);
                Assert.False(DurableTask.Current.AttemptCancellationToken.IsCancellationRequested);
                var refunded = DurableTask.RunInline("refund", static () => 42);
                await DurableTask.Delay(TimeSpan.FromMinutes(1));
                return refunded + await DurableTask.Run(static _ => 1).WithId("child");
            }),
            context);

        Assert.Equal(43, response.GetResult<int>());
        Assert.True(host.Effects.ContainsKey(TaskId.Parse("root/refund")));
        Assert.Contains(ChildId, host.EntryIds);
    }

    [Fact]
    public async Task CompensationFilteredOnTaskCancellationDoesNotRunForAttemptCancellation()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(RootId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compensated = false;
        var released = false;
        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                try
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    compensated = true;
                    throw;
                }
                finally
                {
                    released = true;
                }
            }),
            context).AsTask();
        await started.Task.WaitAsync(WaitTimeout, TestCancellation);

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);

        var response = await run.WaitAsync(WaitTimeout, TestCancellation);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal(context.CancellationToken, Assert.IsAssignableFrom<OperationCanceledException>(response.Exception).CancellationToken);
        Assert.False(compensated);
        Assert.True(released);
    }

    [Theory]
    [MemberData(nameof(DurableOperations))]
    public async Task DurableOperationOfACanceledAttemptThrowsTheAttemptsCancellation(string operation, bool cancelWhileRunning)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(RootId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effectRuns = 0;
        if (!cancelWhileRunning)
        {
            DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskDeleted);
        }

        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ =>
            {
                started.SetResult();
                await resume.Task;
                await RunOperationAsync(operation, () => Interlocked.Increment(ref effectRuns));
            }),
            context).AsTask();
        await started.Task.WaitAsync(WaitTimeout, TestCancellation);
        if (cancelWhileRunning)
        {
            DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskDeleted);
        }

        resume.SetResult();
        var response = await run.WaitAsync(WaitTimeout, TestCancellation);

        AssertAttemptCanceled(context, response, DurableTaskAttemptCancellationReason.TaskDeleted);
        Assert.Equal(0, effectRuns);
        Assert.Empty(host.Effects);
        Assert.Empty(host.EntryIds);
        Assert.Equal(0, host.ScheduleRequestCount);
        Assert.Empty(host.DecisionIds);
        Assert.Null(host.LastDelayDueTime);
    }

    [Theory]
    [MemberData(nameof(HandleOperations))]
    public async Task HandleOperationOfACanceledAttemptThrowsTheAttemptsCancellation(string operation)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var context = host.CreateContext(RootId);
        var childRelease = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                var child = await DurableTask.Run(async _ => await childRelease.Task).WithId("child").ScheduleAsync(cancellationToken);
                scheduled.SetResult();
                await resume.Task;
                await RunHandleOperationAsync(operation, child);
            }),
            context).AsTask();
        try
        {
            await scheduled.Task.WaitAsync(WaitTimeout, TestCancellation);

            DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);
            resume.SetResult();
            var response = await run.WaitAsync(WaitTimeout, TestCancellation);

            AssertAttemptCanceled(context, response, DurableTaskAttemptCancellationReason.ActivationStopping);
            Assert.Equal([ChildId], host.EntryIds);
            Assert.False(host.IsCancellationRequested(ChildId));
            Assert.Empty(host.DecisionIds);
            Assert.Null(host.LastDelayDueTime);
            Assert.Null(host.LastPollTimeout);
        }
        finally
        {
            childRelease.TrySetResult(1);
        }
    }

    [Fact]
    public async Task HandleOperationsOutsideAnExecutionIgnoreAnotherExecutionsCanceledAttempt()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(RootId);
        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);
        var definition = host.CreateRootDefinition<int>(static _ => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(7)));

        var scheduled = await definition.WithId("independent").ScheduleAsync(TestCancellation);

        Assert.Equal(7, await scheduled);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await scheduled.GetStatusAsync(cancellationToken: TestCancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingHandleWaitEndedByTheAttemptsCancellationThrowsTheAttemptsCancellation(bool awaitChildDirectly)
    {
        var context = new LinkingContext(RootId);
        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                if (awaitChildDirectly)
                {
                    return await DurableTask.Run(static _ => 1).WithId("child");
                }

                var child = await DurableTask.Run(static _ => 1).WithId("child").ScheduleAsync(cancellationToken);
                return await child;
            }),
            context).AsTask();
        await context.Handle.WaitStarted.Task.WaitAsync(WaitTimeout, TestCancellation);

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskAborted);

        var response = await run.WaitAsync(WaitTimeout, TestCancellation);
        var exception = AssertAttemptCanceled(context, response, DurableTaskAttemptCancellationReason.TaskAborted);
        Assert.NotNull(context.Handle.RawCancellation);
        Assert.Same(context.Handle.RawCancellation, exception.InnerException);
    }

    [Fact]
    public async Task CallerCancellationOfAPendingHandleWaitThrowsTheCallersCancellation()
    {
        var context = new LinkingContext(RootId);
        using var caller = new CancellationTokenSource();
        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                var child = await DurableTask.Run(static _ => 1).WithId("child").ScheduleAsync(cancellationToken);
                await child.WaitAsync(caller.Token);
            }),
            context).AsTask();
        await context.Handle.WaitStarted.Task.WaitAsync(WaitTimeout, TestCancellation);

        await caller.CancelAsync();

        var response = await run.WaitAsync(WaitTimeout, TestCancellation);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        var exception = Assert.IsType<OperationCanceledException>(response.Exception);
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.False(context.AttemptCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task RunInlineEffectWhichThrowsTheAttemptsCancellationIsNotRecorded()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(RootId);
        DurableTaskAttemptCanceledException? thrown = null;

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.RunInline("effect", new Func<int>(() =>
            {
                // The host stops the attempt while the effect runs, and the effect's work observes it.
                DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);
                thrown = new DurableTaskAttemptCanceledException(
                    DurableTaskAttemptCancellationReason.ActivationStopping,
                    context.AttemptCancellationToken);
                throw thrown;
            }))),
            context);

        Assert.Same(thrown, response.Exception);
        Assert.Empty(host.Effects);

        // A replay runs the effect again, because the canceled attempt recorded nothing.
        var replay = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.RunInline("effect", static () => 2)),
            host.CreateContext(RootId));
        Assert.Equal(2, replay.GetResult<int>());
    }

    [Fact]
    public async Task RunInlineEffectWhichThrowsAnOrdinaryCancellationRecordsIt()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var expected = new OperationCanceledException("effect canceled");

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.RunInline("effect", new Func<int>(() => throw expected))),
            host.CreateContext(RootId));
        var replay = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.RunInline("effect", static () => 2)),
            host.CreateContext(RootId));

        Assert.Same(expected, response.Exception);
        Assert.Same(expected, replay.Exception);
        Assert.Equal(DurableTaskStatus.Canceled, host.Effects[TaskId.Parse("root/effect")].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledResponseFromTheHostOfACanceledAttemptThrowsTheAttemptsCancellation(bool hostRespondsSynchronously)
    {
        var hostCancellation = new OperationCanceledException("The host canceled the delay.");
        var response = new TaskCompletionSource<DurableTaskResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestContext? context = null;
        var host = new TestHost(DateTimeOffset.UnixEpoch)
        {
            DelayCallback = _ =>
            {
                delayStarted.SetResult();
                if (!hostRespondsSynchronously)
                {
                    return new(response.Task);
                }

                // The host stops the attempt while it handles the delay, and reports the delay as canceled.
                DurableTaskRuntimeHelper.CancelAttempt(context!, DurableTaskAttemptCancellationReason.TaskAborted);
                return new(DurableTaskResponse.FromCanceled(hostCancellation));
            },
        };
        context = host.CreateContext(RootId);
        var run = DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.FromMinutes(1)), context).AsTask();
        await delayStarted.Task.WaitAsync(WaitTimeout, TestCancellation);
        if (!hostRespondsSynchronously)
        {
            DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskAborted);
            response.SetResult(DurableTaskResponse.FromCanceled(hostCancellation));
        }

        var exception = AssertAttemptCanceled(
            context,
            await run.WaitAsync(WaitTimeout, TestCancellation),
            DurableTaskAttemptCancellationReason.TaskAborted);
        Assert.Same(hostCancellation, exception.InnerException);
    }

    [Fact]
    public async Task CanceledResponseFromTheHostOfARunningAttemptIsTheRecordedCancellation()
    {
        var hostCancellation = new OperationCanceledException("The host canceled the delay.");
        var host = new TestHost(DateTimeOffset.UnixEpoch)
        {
            DelayCallback = _ => new(DurableTaskResponse.FromCanceled(hostCancellation)),
        };
        var context = host.CreateContext(RootId);

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.FromMinutes(1)), context);

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Same(hostCancellation, response.Exception);
        Assert.False(context.AttemptCancellationToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledChildResponseIsTheAttemptsCancellationOnlyWhenTheAttemptIsCanceled(bool cancelAttempt)
    {
        var childCancellation = new OperationCanceledException("The child was canceled.");
        var context = new RespondingContext(RootId);
        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                var child = await DurableTask.Run(static _ => 1).WithId("child").ScheduleAsync(cancellationToken);
                return await child;
            }),
            context).AsTask();
        await context.Handle.WaitStarted.Task.WaitAsync(WaitTimeout, TestCancellation);

        if (cancelAttempt)
        {
            DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);
        }

        context.Handle.Response.SetResult(DurableTaskResponse.FromCanceled(childCancellation));
        var response = await run.WaitAsync(WaitTimeout, TestCancellation);

        if (cancelAttempt)
        {
            var exception = AssertAttemptCanceled(context, response, DurableTaskAttemptCancellationReason.ActivationStopping);
            Assert.Same(childCancellation, exception.InnerException);
        }
        else
        {
            Assert.Equal(DurableTaskStatus.Canceled, response.Status);
            Assert.Same(childCancellation, response.Exception);
        }
    }

    [Fact]
    public void ExceptionReportsItsReasonAndToken()
    {
        using var source = new CancellationTokenSource();
        var inner = new OperationCanceledException(source.Token);

        var exception = new DurableTaskAttemptCanceledException(DurableTaskAttemptCancellationReason.TaskDeleted, source.Token, inner);
        var defaulted = new DurableTaskAttemptCanceledException();

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(DurableTaskAttemptCancellationReason.TaskDeleted, exception.Reason);
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Same(inner, exception.InnerException);
        Assert.False(string.IsNullOrEmpty(exception.Message));
        Assert.Equal(DurableTaskAttemptCancellationReason.ActivationStopping, defaulted.Reason);
    }

    private static DurableTaskAttemptCanceledException AssertAttemptCanceled(
        DurableTaskContext context,
        DurableTaskResponse response,
        DurableTaskAttemptCancellationReason reason)
    {
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        var exception = Assert.IsType<DurableTaskAttemptCanceledException>(response.Exception);
        Assert.Equal(reason, exception.Reason);
        Assert.Equal(context.AttemptCancellationToken, exception.CancellationToken);
        return exception;
    }

    private static async Task RunOperationAsync(string operation, Action effect)
    {
        DurableTask<int>[] Inputs() => [DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2)];
        switch (operation)
        {
            case "RunInline":
                DurableTask.RunInline("effect", () =>
                {
                    effect();
                    return 1;
                });
                break;
            case "RunInlineAction":
                DurableTask.RunInline(effect);
                break;
            case "AwaitChild":
                await DurableTask.Run(static _ => 1);
                break;
            case "AwaitNamedChild":
                await DurableTask.Run(static _ => 1).WithId("child");
                break;
            case "ScheduleChild":
                await DurableTask.Run(static _ => 1).WithId("child").ScheduleAsync();
                break;
            case "PostChild":
                _ = DurableTask.Run(static _ => { }).Post();
                break;
            case "ShieldedChild":
                await DurableTask.Run(static _ => 1).Shielded();
                break;
            case "DetachedChild":
                await DurableTask.Run(static _ => 1).Detached().ScheduleAsync();
                break;
            case "SignalWait":
                await new DurableSignal<int>("approved").WaitAsync();
                break;
            case "SignalTryWait":
                await new DurableSignal<int>("approved").TryWaitAsync(DateTimeOffset.UnixEpoch.AddHours(1));
                break;
            case "Delay":
                await DurableTask.Delay(TimeSpan.FromMinutes(1));
                break;
            case "DelayUntil":
                await DurableTask.DelayUntil(DateTimeOffset.UnixEpoch.AddHours(1));
                break;
            case "WhenAll":
                await DurableTask.WhenAll(Inputs());
                break;
            case "WhenAny":
                await DurableTask.WhenAny(Inputs());
                break;
            case "WhenEach":
                await foreach (var _ in DurableTask.WhenEach(Inputs()))
                {
                }

                break;
            case "WhenAllCompleted":
                await DurableTask.WhenAllCompleted(Inputs());
                break;
            case "ScheduleAll":
                await DurableTask.ScheduleAll(Inputs());
                break;
            case "Race":
                await DurableTask.Race(Inputs());
                break;
            case "WaitAsyncTimeout":
                await DurableTask.Run(static _ => 1).WaitAsync(TimeSpan.FromMinutes(1));
                break;
            case "WaitUntilAsync":
                await DurableTask.Run(static _ => 1).WaitUntilAsync(DateTimeOffset.UnixEpoch.AddHours(1));
                break;
            case "ConfiguredCancel":
                await DurableTask.Run(static _ => 1).WithId("child").CancelAsync("buyer");
                break;
            case "ConfiguredPoll":
                await DurableTask.Run(static _ => 1).WithId("child").PollAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static async Task RunHandleOperationAsync(string operation, ScheduledTask<int> child)
    {
        switch (operation)
        {
            case "Await":
                await child;
                break;
            case "WaitAsync":
                await child.WaitAsync(TestCancellation);
                break;
            case "GetResponse":
                await child.GetResponseAsync(TestCancellation);
                break;
            case "GetStatus":
                await child.GetStatusAsync(cancellationToken: TestCancellation);
                break;
            case "IsCompleted":
                await child.IsCompletedAsync(cancellationToken: TestCancellation);
                break;
            case "Cancel":
                await child.CancelAsync(TestCancellation);
                break;
            case "CancelWithReason":
                await child.CancelAsync("buyer", TestCancellation);
                break;
            case "Abort":
                await child.AbortAsync("operator", TestCancellation);
                break;
            case "WaitAsyncTimeout":
                await child.WaitAsync(TimeSpan.FromMinutes(1), TestCancellation);
                break;
            case "WhenAll":
                await ScheduledTask.WhenAll(new ScheduledTask[] { child }, TestCancellation);
                break;
            case "WhenAny":
                await ScheduledTask.WhenAny(new ScheduledTask[] { child }, TestCancellation);
                break;
            case "WhenEach":
                await foreach (var _ in ScheduledTask.WhenEach(new ScheduledTask[] { child }))
                {
                }

                break;
            case "Race":
                await ScheduledTask.Race(new ScheduledTask[] { child }, TestCancellation);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    /// <summary>
    /// A host whose handle waits end when the attempt is canceled, as a host which stops the attempt's in-memory work does,
    /// throwing the raw cancellation of the wait.
    /// </summary>
    private sealed class LinkingContext : DurableTaskContext
    {
        public LinkingContext(TaskId taskId)
            : base(taskId)
        {
            Handle = new LinkedHandle(this, taskId.Child("child"));
        }

        public LinkedHandle Handle { get; }

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken) => new(Handle);

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => Handle;

        protected override ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class LinkedHandle(DurableTaskContext owner, TaskId taskId) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCanceledException? RawCancellation { get; private set; }

        public TaskId TaskId => taskId;

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.AttemptCancellationToken);
            WaitStarted.TrySetResult();
            try
            {
                return await _completion.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !owner.AttemptCancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                RawCancellation = exception;
                throw;
            }
        }

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>A host whose child handle's wait returns the response which the test supplies, whatever the attempt's state.</summary>
    private sealed class RespondingContext(TaskId taskId) : DurableTaskContext(taskId)
    {
        public RespondingHandle Handle { get; } = new(taskId.Child("child"));

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken) => new(Handle);

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => Handle;

        protected override ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RespondingHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public TaskCompletionSource<DurableTaskResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskId TaskId => taskId;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            return new(Response.Task);
        }

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
