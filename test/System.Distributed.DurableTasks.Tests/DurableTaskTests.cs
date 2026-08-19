using System.Collections.Concurrent;
using System.Distributed.DurableTasks;
using Xunit;

namespace System.Distributed.DurableTasks.Tests;

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
    public async Task CancellationAttemptsTokenAndDurableCallbacksUsingTheirDocumentedContexts()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var context = host.CreateContext(TaskId.CreateRoot("all-callbacks"));
        var calls = new ConcurrentBag<string>();
        using var tokenRegistration = context.CancellationToken.Register(static state =>
        {
            var observedCalls = (ConcurrentBag<string>)state!;
            Assert.Null(DurableExecutionContext.Current);
            observedCalls.Add("token");
            throw new InvalidOperationException("token failure");
        }, calls);
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Assert.Same(context, DurableExecutionContext.Current);
            calls.Add("durable-1");
            throw new ArgumentException("durable failure");
        });
        await context.RegisterCancellationCallbackAsync(_ =>
        {
            Assert.Same(context, DurableExecutionContext.Current);
            calls.Add("durable-2");
            return ValueTask.CompletedTask;
        });

        var first = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        var second = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await first);

        Assert.Same(first, second);
        Assert.True(first.IsCompleted);
        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Equal(["durable-1", "durable-2", "token"], calls.Order());
        Assert.Null(DurableExecutionContext.Current);
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
}

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

        public void StartOnce(Func<Task<DurableTaskResponse>> start)
        {
            lock (_lock)
            {
                _response ??= start();
            }
        }

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
            => await (_response ?? Task.FromResult(DurableTaskResponse.Pending)).WaitAsync(cancellationToken);

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
