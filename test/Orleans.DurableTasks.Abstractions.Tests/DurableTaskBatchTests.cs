using System.Collections.Concurrent;
using System.Globalization;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Verifies scheduled task inspection, <c>ScheduleAll</c>, <c>WhenAllCompleted</c>, deterministic <c>ScheduledTask.WhenAll</c>
/// failures, and <c>WhenEach</c> ordering.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskBatchTests
{
    private static readonly TaskId RootId = TaskId.CreateRoot("root");

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandleReportsPendingUntilItObservesTheTerminalResponse()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = host.CreateRootDefinition<int>(async _ =>
        {
            var handle = await DurableTask.Run(async _ => await release.Task).WithId("child").ScheduleAsync();
            Assert.False(handle.IsCompleted);
            Assert.Equal(DurableTaskStatus.Pending, handle.Status);
            Assert.Null(handle.Exception);
            Assert.False(handle.TryGetResult(out int _));
            Assert.Throws<InvalidOperationException>(() => handle.GetResult());

            release.SetResult(42);
            Assert.Equal(42, await handle);
            Assert.True(handle.IsCompleted);
            Assert.Equal(DurableTaskStatus.CompletedSuccessfully, handle.Status);
            Assert.Null(handle.Exception);
            Assert.True(handle.TryGetResult(out var value));
            Assert.Equal(42, value);
            return DurableTaskResponse.FromResult(handle.GetResult());
        });

        Assert.Equal(42, await await root.WithId("root").ScheduleAsync(TestCancellation));
    }

    [Fact]
    public async Task HandleExposesTheRecordedFailureOrCancellationOnceObserved()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<int>(async _ =>
        {
            var failed = await DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("failed")))
                .WithId("failed")
                .ScheduleAsync();
            var canceled = await DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled")))
                .WithId("canceled")
                .ScheduleAsync();

            // A wait which returns the response, or a poll which observes it, caches it in the handle.
            Assert.Equal(DurableTaskStatus.Failed, (await failed.GetResponseAsync()).Status);
            Assert.True(await canceled.IsCompletedAsync(new PollingOptions { PollTimeout = TimeSpan.FromSeconds(10) }));

            Assert.Equal(DurableTaskStatus.Failed, failed.Status);
            Assert.Equal("failed", Assert.IsType<InvalidOperationException>(failed.Exception).Message);
            Assert.False(failed.TryGetResult(out int _));
            Assert.Equal("failed", Assert.Throws<InvalidOperationException>(() => failed.GetResult()).Message);

            Assert.Equal(DurableTaskStatus.Canceled, canceled.Status);
            Assert.Equal("canceled", Assert.IsAssignableFrom<OperationCanceledException>(canceled.Exception).Message);
            Assert.False(canceled.TryGetResult(out int _));
            Assert.ThrowsAny<OperationCanceledException>(() => canceled.GetResult());
            return DurableTaskResponse.FromResult(0);
        });

        Assert.Equal(0, await await root.WithId("root").ScheduleAsync(TestCancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduleAllReturnsTheHandlesInInputOrderBeforeTheResults(bool named)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releases = CreateReleases(2);
        var root = host.CreateRootDefinition<int[]>(async _ =>
        {
            var batch = DurableTask.ScheduleAll(Inputs(releases));
            var handles = named ? await batch.WithId("batch") : await batch;
            Assert.All(handles, static handle => Assert.False(handle.IsCompleted));

            releases[1].SetResult(11);
            releases[0].SetResult(10);
            return DurableTaskResponse.FromResult(new[] { await handles[0], await handles[1] });
        });

        int[] results = await await root.WithId("root").ScheduleAsync(TestCancellation);
        Assert.Equal([10, 11], results);
        var operation = named ? "batch" : "$schedule-all-1";
        Assert.Equal(
            [RootId, TaskId.Parse($"root/{operation}/0"), TaskId.Parse($"root/{operation}/1")],
            host.EntryIds);
    }

    [Fact]
    public async Task UntypedScheduleAllAcceptsAnySequence()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<int>(async _ =>
        {
            var handles = await DurableTask.ScheduleAll(Enumerable.Range(0, 3).Select(static _ => DurableTask.Run(static _ => { })));
            await ScheduledTask.WhenAll(handles);
            return DurableTaskResponse.FromResult(handles.Length);
        });

        Assert.Equal(3, await await root.WithId("root").ScheduleAsync(TestCancellation));
        Assert.True(host.Contains(TaskId.Parse("root/$schedule-all-1/2")));
    }

    [Fact]
    public async Task HandleCombinatorsRunOnlyWhenAwaitedWithinAnExecution()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            var exception = await Assert.ThrowsAsync<NotSupportedException>(
                () => DurableTask.ScheduleAll([DurableTask.Run(static _ => 1)]).WithId("batch").ScheduleAsync(TestCancellation));
            await Assert.ThrowsAsync<NotSupportedException>(
                () => DurableTask.WhenAllCompleted([DurableTask.Run(static _ => 1)]).WithId("completed").ScheduleAsync(TestCancellation));
            return DurableTaskResponse.FromResult(exception.Message);
        });

        Assert.Contains("scheduled task handles", await await root.WithId("root").ScheduleAsync(TestCancellation), StringComparison.Ordinal);
        Assert.Equal([RootId], host.EntryIds);

        // A host which runs the definition as an execution's body receives a failed response.
        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.ScheduleAll([DurableTask.Run(static _ => 1)]),
            host.CreateContext(TaskId.CreateRoot("other")));
        Assert.IsType<NotSupportedException>(response.Exception);
        Assert.Throws<InvalidOperationException>(() => DurableTask.WhenEach([DurableTask.Run(static _ => 1)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAllCompletedWaitsForEveryInputAndNeverThrowsForTheirOutcomes(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ObservedDurableTask<int>[] inputs =
        [
            new(DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("failed")))),
            new(DurableTask.Run(async _ =>
            {
                await releaseSlow.Task;
                return 2;
            })),
            new(DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled")))),
        ];
        var root = host.CreateRootDefinition<DurableTaskStatus[]>(async _ =>
        {
            var completed = generic
                ? await DurableTask.WhenAllCompleted<int>(inputs).WithId("reserve")
                : await DurableTask.WhenAllCompleted((IReadOnlyList<DurableTask>)inputs).WithId("reserve");
            Assert.All(completed, static task => Assert.True(task.IsCompleted));
            Assert.Equal("failed", completed[0].Exception!.Message);
            Assert.Equal("canceled", completed[2].Exception!.Message);
            if (generic)
            {
                var typed = (ScheduledTask<int>)completed[1];
                Assert.True(typed.TryGetResult(out var value));
                Assert.Equal(2, value);
            }

            return DurableTaskResponse.FromResult(completed.Select(static task => task.Status).ToArray());
        });

        var execution = (await root.WithId("root").ScheduleAsync(TestCancellation)).GetResponseAsync(TestCancellation);

        // The failed and canceled inputs complete first; the combinator waits for the slow input.
        Assert.Equal(DurableTaskStatus.Failed, (await ObserveAsync(inputs[0])).Status);
        Assert.Equal(DurableTaskStatus.Canceled, (await ObserveAsync(inputs[2])).Status);
        Assert.False(execution.IsCompleted);
        releaseSlow.SetResult();

        Assert.Equal(
            [DurableTaskStatus.Failed, DurableTaskStatus.CompletedSuccessfully, DurableTaskStatus.Canceled],
            (await execution.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation)).GetResult<DurableTaskStatus[]>());
        Assert.True(host.Contains(TaskId.Parse("root/reserve/2")));
    }

    [Fact]
    public async Task ScheduledTaskWhenAllCompletedReturnsTheHandlesInInputOrder()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<DurableTaskStatus[]>(async _ =>
        {
            var handles = await DurableTask.ScheduleAll(
            [
                DurableTask.Run(static _ => 1),
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("failed"))),
            ]);
            var completed = await ScheduledTask.WhenAllCompleted(handles);
            var untyped = await ScheduledTask.WhenAllCompleted((IReadOnlyList<ScheduledTask>)handles);
            Assert.Equal(handles, completed);
            Assert.Equal<ScheduledTask>(handles, untyped);
            return DurableTaskResponse.FromResult(completed.Select(static task => task.Status).ToArray());
        });

        Assert.Equal(
            [DurableTaskStatus.CompletedSuccessfully, DurableTaskStatus.Failed],
            await await root.WithId("root").ScheduleAsync(TestCancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledTaskWhenAllThrowsTheFirstFailureInInputOrderInAnyCompletionOrder(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            var handles = await DurableTask.ScheduleAll(
            [
                DurableTask.Run(async _ =>
                {
                    await releaseSlow.Task;
                    return 1;
                }),
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled input"))),
                DurableTask.Run((Func<CancellationToken, Task<int>>)(async _ =>
                {
                    await releaseFirstFailure.Task;
                    throw new InvalidOperationException("first failure");
                })),
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("second failure"))),
            ]);

            // The canceled input and the later failure complete before the first failure, which completes before the
            // slow input.
            await handles[1].GetResponseAsync();
            await handles[3].GetResponseAsync();
            var whenAll = generic ? ScheduledTask.WhenAll(handles) : ScheduledTask.WhenAll((IReadOnlyList<ScheduledTask>)handles);
            releaseFirstFailure.SetResult();
            await handles[2].GetResponseAsync();
            Assert.False(whenAll.IsCompleted);
            releaseSlow.SetResult();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => whenAll);
            Assert.True(handles[0].IsCompleted);
            return DurableTaskResponse.FromResult(exception.Message);
        });

        Assert.Equal("first failure", await await root.WithId("root").ScheduleAsync(TestCancellation));
    }

    [Fact]
    public async Task ScheduledTaskWhenAllThrowsTheFirstCancellationWhenNoInputFailed()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            var handles = await DurableTask.ScheduleAll(
            [
                DurableTask.Run(static _ => 1),
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("first"))),
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("second"))),
            ]);
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScheduledTask.WhenAll(handles, TestCancellation));
            return DurableTaskResponse.FromResult(exception.Message);
        });

        Assert.Equal("first", await await root.WithId("root").ScheduleAsync(TestCancellation));
    }

    [Fact]
    public async Task WhenEachYieldsInCompletionOrderAndAReplayYieldsTheSameOrder()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releases = CreateReleases(3);
        var yields = Enumerable.Range(0, 3)
            .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var attempts = new ConcurrentQueue<int[]>();
        var definition = DurableTask.Run(async _ =>
        {
            var order = new List<int>();
            await foreach (var task in DurableTask.WhenEach(Inputs(releases)))
            {
                Assert.True(task.IsCompleted);
                Assert.Equal(10 + IndexOf(task), task.GetResult());
                order.Add(IndexOf(task));
                yields[order.Count - 1].TrySetResult();
            }

            attempts.Enqueue([.. order]);
        });

        var first = DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId)).AsTask();
        foreach (var (index, step) in new[] { (2, 0), (0, 1), (1, 2) })
        {
            releases[index].SetResult(10 + index);
            await yields[step].Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);
        }

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await first.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation)).Status);

        // The replay finds every input complete; the recorded selections yield the original order.
        var replay = await DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId));

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, replay.Status);
        Assert.Equal([[2, 0, 1], [2, 0, 1]], attempts);
        TaskId[] decisions = [.. Enumerable.Range(0, 3).Select(static step => TaskId.Parse($"root/$when-each-1/$next-{step}"))];
        Assert.Equal([.. decisions, .. decisions], host.DecisionIds);
    }

    [Fact]
    public async Task WhenEachEnumeratorCancellationAbandonsPendingScheduling()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestHost(DateTimeOffset.UnixEpoch) { ScheduleGate = gate.Task };
        using var cancellation = new CancellationTokenSource();
        var root = host.CreateRootDefinition<bool>(async _ =>
        {
            var enumerator = DurableTask.WhenEach([DurableTask.Run(static _ => 1)]).GetAsyncEnumerator(cancellation.Token);
            var moveNext = enumerator.MoveNextAsync().AsTask();
            Assert.False(moveNext.IsCompleted);

            await cancellation.CancelAsync();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => moveNext.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation));
            await enumerator.DisposeAsync();
            gate.SetResult();
            return DurableTaskResponse.FromResult(exception.CancellationToken.IsCancellationRequested);
        });

        Assert.True(await await root.WithId("root").ScheduleAsync(TestCancellation));
        Assert.Equal(1, host.ScheduleRequestCount);
    }

    [Fact]
    public async Task ScheduledTaskWhenEachYieldsFailuresAndCancellationsWithoutThrowing()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<DurableTaskStatus[]>(async _ =>
        {
            var handles = await DurableTask.ScheduleAll(
            [
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("failed"))),
                DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled"))),
            ]).WithId("batch");
            var statuses = new List<DurableTaskStatus>();
            await foreach (var task in ScheduledTask.WhenEach(handles))
            {
                statuses.Add(task.Status);
            }

            return DurableTaskResponse.FromResult(statuses.Order().ToArray());
        });

        Assert.Equal(
            [DurableTaskStatus.Canceled, DurableTaskStatus.Failed],
            await await root.WithId("root").ScheduleAsync(TestCancellation));
        Assert.Equal(
            [TaskId.Parse("root/$when-each-1/$next-0"), TaskId.Parse("root/$when-each-1/$next-1")],
            host.DecisionIds);
    }

    [Fact]
    public async Task ScheduledTaskWhenEachOutsideAnExecutionYieldsInObservedOrder()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releases = CreateReleases(3);
        var handles = new ScheduledTask<int>[3];
        for (var index = 0; index < handles.Length; index++)
        {
            var release = releases[index];
            handles[index] = await host.CreateRootDefinition<int>(async _ => DurableTaskResponse.FromResult(await release.Task))
                .WithId($"root-{index}").ScheduleAsync(TestCancellation);
        }

        await using var enumerator = ScheduledTask.WhenEach(handles).GetAsyncEnumerator(TestCancellation);
        var order = new List<int>();
        foreach (var index in new[] { 1, 2, 0 })
        {
            var next = enumerator.MoveNextAsync().AsTask();
            releases[index].SetResult(index);
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation));
            Assert.True(enumerator.Current.IsCompleted);
            order.Add(enumerator.Current.GetResult());
        }

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal([1, 2, 0], order);
        Assert.Empty(host.DecisionIds);
    }

    private static TaskCompletionSource<int>[] CreateReleases(int count) =>
        Enumerable.Range(0, count)
            .Select(static _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

    private static DurableTask<int>[] Inputs(TaskCompletionSource<int>[] releases) =>
        releases.Select(static release => DurableTask.Run(async _ => await release.Task)).ToArray();

    private static int IndexOf(ScheduledTask task)
    {
        var id = task.Id.ToString();
        return int.Parse(id[(id.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
    }

    private static Task<DurableTaskResponse> ObserveAsync<TResult>(ObservedDurableTask<TResult> input)
        => input.Response.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);

    /// <summary>Runs an input definition and publishes its terminal response before the host records it.</summary>
    private sealed class ObservedDurableTask<TResult>(DurableTask<TResult> inner) : DurableTask<TResult>
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DurableTaskResponse> Response => _response.Task;

        protected override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        {
            var response = await DurableTaskRuntimeHelper.RunAsync(inner, context);
            _response.SetResult(response);
            return response;
        }
    }
}
