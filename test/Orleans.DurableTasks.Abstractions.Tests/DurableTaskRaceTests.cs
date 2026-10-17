using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Verifies <c>Race</c>, which cancels and awaits the inputs which lose, and the timeouts which <c>WaitAsync</c> and
/// <c>WaitUntilAsync</c> build on it, for definitions and for scheduled task handles.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskRaceTests
{
    private const string RaceLostReason = "Another task won the race.";
    private const string TimeoutReason = "The operation timed out after 00:05:00.";
    private static readonly TaskId RootId = TaskId.CreateRoot("root");
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RaceReturnsTheWinnerOnceEveryOtherInputHasSettled(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var canceled = new Holder(returnsReason: false);
        canceled.Settle();
        var returning = new Holder(returnsReason: true);
        DurableTask<string>[] inputs = [DurableTask.Run(static _ => "winner"), canceled.Definition, returning.Definition];

        var execution = DurableTaskRuntimeHelper.RunAsync(
            generic ? DurableTask.Race<string>(inputs) : DurableTask.Race((IReadOnlyList<DurableTask>)inputs),
            host.CreateContext(RootId)).AsTask();

        // Each loser observes the race's reason, and the race waits for the loser which is still settling.
        Assert.Equal(RaceLostReason, await canceled.Reason.WaitAsync(WaitTimeout, TestCancellation));
        Assert.Equal(RaceLostReason, await returning.Reason.WaitAsync(WaitTimeout, TestCancellation));
        Assert.False(execution.IsCompleted);
        returning.Settle();
        var response = await execution.WaitAsync(WaitTimeout, TestCancellation);

        if (generic)
        {
            Assert.Equal((0, "winner"), response.GetResult<(int, string)>());
        }
        else
        {
            Assert.Equal(0, response.GetResult<int>());
        }

        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/$race-1/1")).Status);
        Assert.Equal(RaceLostReason, (await WaitForEntryAsync(host, "root/$race-1/2")).GetResult<string>());
        Assert.False(host.IsCancellationRequested(TaskId.Parse("root/$race-1/0")));
        Assert.Equal(RaceLostReason, host.GetEntry(TaskId.Parse("root/$race-1/1")).CancellationReason);
    }

    [Fact]
    public async Task RaceFailsWithTheExceptionOfAWinnerWhichFailedOnceTheOtherInputsHaveSettled()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var loser = new Holder(returnsReason: false);
        loser.Settle();

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Race(
            [
                DurableTask.Run((Func<CancellationToken, string>)(static _ => throw new InvalidOperationException("declined"))),
                loser.Definition,
            ]),
            host.CreateContext(RootId)).AsTask().WaitAsync(WaitTimeout, TestCancellation);

        Assert.Equal("declined", Assert.IsType<InvalidOperationException>(response.Exception).Message);
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/$race-1/1")).Status);
    }

    [Fact]
    public async Task RaceReplaySelectsTheRecordedWinnerUnderItsName()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var releases = Enumerable.Range(0, 2)
            .Select(static _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var definition = DurableTask.Run(async _ =>
            await DurableTask.Race(releases.Select(static release => DurableTask.Run(async cancellationToken => await release.Task.WaitAsync(cancellationToken))))
                .WithId("quote"));

        var live = DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId)).AsTask();
        releases[1].SetResult(1);
        Assert.Equal((1, 1), (await live.WaitAsync(WaitTimeout, TestCancellation)).GetResult<(int, int)>());

        // Every input is terminal when the execution replays; the recorded selection returns the same winner.
        releases[0].SetResult(0);
        var replay = await DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId));

        Assert.Equal((1, 1), replay.GetResult<(int, int)>());
        Assert.Equal([TaskId.Parse("root/quote/0"), TaskId.Parse("root/quote/1")], host.EntryIds);
        Assert.Equal([TaskId.Parse("root/quote/$winner"), TaskId.Parse("root/quote/$winner")], host.DecisionIds);
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/quote/0")).Status);
        Assert.Equal(RaceLostReason, host.GetEntry(TaskId.Parse("root/quote/0")).CancellationReason);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task WaitAsyncReturnsTheOutcomeOfATaskWhichCompletesFirstAndCancelsTheDelay(string outcome)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true, DelayCallback = PendingDelay };
        var task = DurableTask.Run<string>(_ => outcome switch
        {
            "result" => "paid",
            "failure" => throw new InvalidOperationException("declined"),
            _ => throw new OperationCanceledException("withdrawn"),
        });

        var response = await DurableTaskRuntimeHelper.RunAsync(task.WaitAsync(FiveMinutes), host.CreateContext(RootId))
            .AsTask()
            .WaitAsync(WaitTimeout, TestCancellation);

        switch (outcome)
        {
            case "result":
                Assert.Equal("paid", response.GetResult<string>());
                break;
            case "failure":
                Assert.Equal("declined", Assert.IsType<InvalidOperationException>(response.Exception).Message);
                break;
            default:
                Assert.Equal("withdrawn", Assert.IsType<OperationCanceledException>(response.Exception).Message);
                break;
        }

        Assert.Equal(DateTimeOffset.UnixEpoch + FiveMinutes, host.LastDelayDueTime);
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/$timeout-1/1")).Status);
        Assert.False(host.IsCancellationRequested(TaskId.Parse("root/$timeout-1/0")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAsyncThrowsTimeoutExceptionOnceTheTaskWhichTheTimeoutCanceledHasSettled(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var holder = new Holder(returnsReason: false);

        var execution = DurableTaskRuntimeHelper.RunAsync(
            generic ? holder.Definition.WaitAsync(FiveMinutes) : ((DurableTask)holder.Definition).WaitAsync(FiveMinutes),
            host.CreateContext(RootId)).AsTask();
        Assert.Equal(TimeoutReason, await holder.Reason.WaitAsync(WaitTimeout, TestCancellation));
        Assert.False(execution.IsCompleted);
        holder.Settle();
        var response = await execution.WaitAsync(WaitTimeout, TestCancellation);

        var exception = Assert.IsType<TimeoutException>(response.Exception);
        Assert.Equal(TimeoutReason, exception.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/$timeout-1/0")).Status);
    }

    [Fact]
    public async Task WaitAsyncReturnsAResultWhichTheTaskReturnsAfterTheTimeout()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var holder = new Holder(returnsReason: true);
        holder.Settle();

        var response = await DurableTaskRuntimeHelper.RunAsync(holder.Definition.WaitAsync(FiveMinutes), host.CreateContext(RootId))
            .AsTask()
            .WaitAsync(WaitTimeout, TestCancellation);

        Assert.Equal(TimeoutReason, response.GetResult<string>());
    }

    [Fact]
    public async Task WaitAsyncThrowsTheExceptionOfADelayWhichEndsWithoutElapsing()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch)
        {
            PropagatesCancellation = true,
            DelayCallback = static _ => new(DurableTaskResponse.Canceled),
        };
        var holder = new Holder(returnsReason: true);
        holder.Settle();

        var response = await DurableTaskRuntimeHelper.RunAsync(holder.Definition.WaitAsync(FiveMinutes), host.CreateContext(RootId))
            .AsTask()
            .WaitAsync(WaitTimeout, TestCancellation);

        // The task observes a cancellation request without the timeout's reason, and its result is discarded.
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Null(await holder.Reason.WaitAsync(WaitTimeout, TestCancellation));
        Assert.Null((await WaitForEntryAsync(host, "root/$timeout-1/0")).GetResult<string>());
    }

    [Fact]
    public async Task WaitUntilAsyncRequestsCancellationWithTheDeadlineReason()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var holder = new Holder(returnsReason: false);
        holder.Settle();
        var dueTime = DateTimeOffset.UnixEpoch.AddHours(1);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await holder.Definition.WaitUntilAsync(dueTime).WithId("approval")),
            host.CreateContext(RootId))
            .AsTask()
            .WaitAsync(WaitTimeout, TestCancellation);

        const string reason = "The operation timed out because its deadline 1970-01-01T01:00:00.0000000+00:00 passed.";
        Assert.Equal(reason, Assert.IsType<TimeoutException>(response.Exception).Message);
        Assert.Equal(reason, await holder.Reason.WaitAsync(WaitTimeout, TestCancellation));
        Assert.Equal(dueTime, host.LastDelayDueTime);
        Assert.Equal([TaskId.Parse("root/approval/0"), TaskId.Parse("root/approval/1")], host.EntryIds);
        Assert.Equal([TaskId.Parse("root/approval/$winner")], host.DecisionIds);
    }

    [Fact]
    public async Task TimeoutCountsFromTheFirstRunAcrossReplay()
    {
        var delayScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = new TaskCompletionSource<DurableTaskResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0;
        var host = new TestHost(DateTimeOffset.UnixEpoch)
        {
            PropagatesCancellation = true,
            DelayCallback = cancellationToken =>
            {
                Interlocked.Increment(ref delays);
                delayScheduled.TrySetResult();
                return new(elapsed.Task.WaitAsync(cancellationToken));
            },
        };
        var holder = new Holder(returnsReason: false);
        holder.Settle();
        var definition = DurableTask.Run(async _ => await holder.Definition.WaitAsync(FiveMinutes).WithId("pay"));

        // The host cancels the first attempt while it waits, and replays it three minutes later.
        var firstContext = host.CreateContext(RootId);
        var first = DurableTaskRuntimeHelper.RunAsync(definition, firstContext).AsTask();
        await delayScheduled.Task.WaitAsync(WaitTimeout, TestCancellation);
        DurableTaskRuntimeHelper.CancelAttempt(firstContext, DurableTaskAttemptCancellationReason.ActivationStopping);
        var canceled = await first.WaitAsync(WaitTimeout, TestCancellation);
        Assert.Equal(
            DurableTaskAttemptCancellationReason.ActivationStopping,
            Assert.IsType<DurableTaskAttemptCanceledException>(canceled.Exception).Reason);
        host.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(3);
        var replay = DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId)).AsTask();
        elapsed.SetResult(DurableTaskResponse.Completed);
        var response = await replay.WaitAsync(WaitTimeout, TestCancellation);

        Assert.Equal(TimeoutReason, Assert.IsType<TimeoutException>(response.Exception).Message);
        Assert.Equal(1, Volatile.Read(ref delays));
        Assert.Equal(DateTimeOffset.UnixEpoch + FiveMinutes, host.LastDelayDueTime);
        Assert.Equal([TaskId.Parse("root/pay/0"), TaskId.Parse("root/pay/1")], host.EntryIds);
    }

    [Fact]
    public async Task HandleWaitAsyncWithinAnExecutionTimesOutThroughADurableDelay()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var holder = new Holder(returnsReason: false);
        holder.Settle();
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            var payment = await holder.Definition.WithId("pay").ScheduleAsync();
            try
            {
                return DurableTaskResponse.FromResult(await payment.WaitAsync(FiveMinutes, TestCancellation));
            }
            catch (TimeoutException exception) when (exception.InnerException is OperationCanceledException)
            {
                return DurableTaskResponse.FromResult($"{exception.Message} {payment.Status}");
            }
        });

        Assert.Equal($"{TimeoutReason} Canceled", await await root.WithId("root").ScheduleAsync(TestCancellation).WaitAsync(WaitTimeout, TestCancellation));
        Assert.Equal(DateTimeOffset.UnixEpoch + FiveMinutes, host.LastDelayDueTime);
        Assert.Equal(
            [RootId, TaskId.Parse("root/$timeout-1/1"), TaskId.Parse("root/pay")],
            host.EntryIds);
        Assert.Equal([TaskId.Parse("root/$timeout-1/$winner")], host.DecisionIds);
    }

    [Fact]
    public async Task HandleWaitUntilAsyncWithinAnExecutionReturnsWhenTheTaskCompletesFirst()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true, DelayCallback = PendingDelay };
        var dueTime = DateTimeOffset.UnixEpoch.AddHours(1);
        var root = host.CreateRootDefinition<DurableTaskStatus>(async _ =>
        {
            var step = await DurableTask.Run(static _ => { }).WithId("step").ScheduleAsync();
            await step.WaitUntilAsync(dueTime, TestCancellation);
            return DurableTaskResponse.FromResult(step.Status);
        });

        Assert.Equal(
            DurableTaskStatus.CompletedSuccessfully,
            await await root.WithId("root").ScheduleAsync(TestCancellation).WaitAsync(WaitTimeout, TestCancellation));
        Assert.Equal(dueTime, host.LastDelayDueTime);
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/$timeout-1/1")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleWaitAsyncOutsideAnExecutionCancelsTheTaskWhenTheTimeoutElapses(bool returnsReason)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var scheduled = await host.CreateRootDefinition<string>(async context =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                return DurableTaskResponse.FromResult("completed");
            }
            catch (OperationCanceledException) when (returnsReason && context.IsTaskCancellationRequested)
            {
                return DurableTaskResponse.FromResult(context.TaskCancellationReason!);
            }
        }).WithId("root").ScheduleAsync(TestCancellation);

        const string reason = "The operation timed out after 00:00:00.";
        if (returnsReason)
        {
            Assert.Equal(reason, await scheduled.WaitAsync(TimeSpan.Zero, TestCancellation).WaitAsync(WaitTimeout, TestCancellation));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(
                () => scheduled.WaitAsync(TimeSpan.Zero, TestCancellation).WaitAsync(WaitTimeout, TestCancellation));
            Assert.Equal(reason, exception.Message);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }

        Assert.Equal(reason, host.GetEntry(RootId).CancellationReason);
        Assert.Null(host.LastDelayDueTime);
        Assert.Empty(host.DecisionIds);
    }

    [Fact]
    public async Task HandleWaitOutsideAnExecutionReturnsACompletedResultAndHonorsTheCallersToken()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var completed = await host.CreateRootDefinition<int>(static _ => new(DurableTaskResponse.FromResult(7)))
            .WithId("completed").ScheduleAsync(TestCancellation);
        var pending = await host.CreateRootDefinition<int>(static async context =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return DurableTaskResponse.FromResult(0);
        }).WithId("pending").ScheduleAsync(TestCancellation);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        Assert.Equal(7, await completed.WaitUntilAsync(DateTimeOffset.MinValue, TestCancellation));
        await ((ScheduledTask)completed).WaitAsync(TimeSpan.Zero, TestCancellation);
        var abandoned = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromHours(1), canceled.Token));
        Assert.Equal(canceled.Token, abandoned.CancellationToken);
        Assert.False(host.IsCancellationRequested(TaskId.CreateRoot("pending")));
        await pending.CancelAsync(TestCancellation);
    }

    [Fact]
    public async Task RaceOfHandlesCancelsAndAwaitsTheOtherTasks()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true };
        var loser = new Holder(returnsReason: true);
        loser.Settle();
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            var quotes = await DurableTask.ScheduleAll([DurableTask.Run(static _ => "ground"), loser.Definition]).WithId("quotes");
            var winner = await ScheduledTask.Race(quotes, TestCancellation);
            return DurableTaskResponse.FromResult($"{winner.GetResult()} {quotes[1].GetResult()}");
        });

        Assert.Equal($"ground {RaceLostReason}", await await root.WithId("root").ScheduleAsync(TestCancellation).WaitAsync(WaitTimeout, TestCancellation));
        Assert.Equal([TaskId.Parse("root/$race-1/$winner")], host.DecisionIds);

        // Outside an execution, the first observed response wins and no selection is recorded.
        var first = await host.CreateRootDefinition<string>(static _ => new(DurableTaskResponse.FromResult("first")))
            .WithId("first").ScheduleAsync(TestCancellation);
        var second = await host.CreateRootDefinition<string>(static async context =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return DurableTaskResponse.FromResult("second");
        }).WithId("second").ScheduleAsync(TestCancellation);

        var observed = await ScheduledTask.Race([second, first], TestCancellation).WaitAsync(WaitTimeout, TestCancellation);

        Assert.Same(first, observed);
        Assert.Equal(DurableTaskStatus.Canceled, second.Status);
        Assert.Equal(RaceLostReason, host.GetEntry(TaskId.CreateRoot("second")).CancellationReason);
        Assert.Single(host.DecisionIds);
    }

    [Fact]
    public async Task NestedRacesAndTimeoutsKeepTheirNamedIdentifiers()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch) { PropagatesCancellation = true, DelayCallback = PendingDelay };
        var loser = new Holder(returnsReason: false);
        loser.Settle();
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            var (index, quote) = await DurableTask.Race([DurableTask.Run(static _ => "ground").WaitAsync(FiveMinutes), loser.Definition])
                .WithId("race");
            var paid = await DurableTask.Run(static _ => "paid").WaitAsync(FiveMinutes).WithId("pay");
            return DurableTaskResponse.FromResult($"{index} {quote} {paid}");
        });

        Assert.Equal("0 ground paid", await await root.WithId("root").ScheduleAsync(TestCancellation).WaitAsync(WaitTimeout, TestCancellation));

        // The timeout which races as input 0 runs as a child with its own execution, which names its inputs.
        Assert.Equal(
            [
                RootId,
                TaskId.Parse("root/pay/0"),
                TaskId.Parse("root/pay/1"),
                TaskId.Parse("root/race/0"),
                TaskId.Parse("root/race/0/$timeout-1/0"),
                TaskId.Parse("root/race/0/$timeout-1/1"),
                TaskId.Parse("root/race/1"),
            ],
            host.EntryIds);
        Assert.Equal(
            [TaskId.Parse("root/pay/$winner"), TaskId.Parse("root/race/$winner"), TaskId.Parse("root/race/0/$timeout-1/$winner")],
            host.DecisionIds.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
        Assert.Equal(DurableTaskStatus.Canceled, (await WaitForEntryAsync(host, "root/race/1")).Status);
    }

    [Fact]
    public async Task ArgumentsAreValidated()
    {
        var task = DurableTask.Run(static _ => 1);
        Assert.Throws<ArgumentOutOfRangeException>("timeout", () => task.WaitAsync(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>("timeout", () => ((DurableTask)task).WaitAsync(Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentNullException>("task", () => ((DurableTask<int>)null!).WaitAsync(TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>("task", () => ((DurableTask)null!).WaitAsync(TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>("task", () => ((DurableTask<int>)null!).WaitUntilAsync(DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentNullException>("task", () => ((DurableTask)null!).WaitUntilAsync(DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentNullException>("tasks", () => DurableTask.Race((IReadOnlyList<DurableTask>)null!));
        Assert.Throws<ArgumentNullException>("tasks", () => DurableTask.Race((IEnumerable<DurableTask<int>>)null!));
        Assert.Throws<ArgumentOutOfRangeException>("tasks", () => DurableTask.Race(Array.Empty<DurableTask>()));
        Assert.Throws<ArgumentOutOfRangeException>("tasks", () => DurableTask.Race(Enumerable.Empty<DurableTask<int>>()));
        _ = task.WaitAsync(TimeSpan.Zero);

        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var scheduled = await host.CreateRootDefinition<int>(static _ => new(DurableTaskResponse.FromResult(1)))
            .WithId("root").ScheduleAsync(TestCancellation);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("timeout", () => scheduled.WaitAsync(Timeout.InfiniteTimeSpan, TestCancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            "timeout",
            () => ((ScheduledTask)scheduled).WaitAsync(TimeSpan.FromTicks(-1), TestCancellation));
        await Assert.ThrowsAsync<ArgumentNullException>("tasks", () => ScheduledTask.Race((IReadOnlyList<ScheduledTask>)null!, TestCancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("tasks", () => ScheduledTask.Race(Array.Empty<ScheduledTask<int>>(), TestCancellation));
    }

    /// <summary>A delay which ends canceled when the host cancels it, and doesn't elapse otherwise.</summary>
    private static async ValueTask<DurableTaskResponse> PendingDelay(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return DurableTaskResponse.Completed;
    }

    private static Task<DurableTaskResponse> WaitForEntryAsync(TestHost host, string id)
        => host.GetEntry(TaskId.Parse(id)).WaitAsync(TestCancellation).AsTask().WaitAsync(WaitTimeout, TestCancellation);

    /// <summary>
    /// Defines a task which runs until its durable cancellation is requested, reports the request's reason, and once
    /// <see cref="Settle"/> is called, returns the reason or ends canceled.
    /// </summary>
    private sealed class Holder(bool returnsReason)
    {
        private readonly TaskCompletionSource _settle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> _reason = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> Reason => _reason.Task;

        public DurableTask<string> Definition => DurableTask.Run(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "completed";
            }
            catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
            {
                var reason = DurableTask.Current.TaskCancellationReason;
                _reason.TrySetResult(reason);
                await _settle.Task;
                if (returnsReason)
                {
                    return reason!;
                }

                throw;
            }
        });

        public void Settle() => _settle.TrySetResult();
    }
}
