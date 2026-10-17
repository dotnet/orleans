using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Verifies combinator outcomes, enumerable inputs, and operation naming with <c>WithId</c>.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskCombinatorTests
{
    private static readonly TaskId RootId = TaskId.CreateRoot("root");

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAllWaitsForEveryInputThenThrowsTheFirstFailureInInputOrder(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ObservedDurableTask<int>[] inputs =
        [
            new(DurableTask.Run(async _ =>
            {
                await releaseSlow.Task;
                return 1;
            })),
            new(DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled input")))),
            new(DurableTask.Run((Func<CancellationToken, Task<int>>)(async _ =>
            {
                await releaseFirstFailure.Task;
                throw new InvalidOperationException("first failure");
            }))),
            new(DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("second failure")))),
        ];
        var execution = DurableTaskRuntimeHelper.RunAsync(
            generic ? DurableTask.WhenAll<int>(inputs) : DurableTask.WhenAll((IReadOnlyList<DurableTask>)inputs),
            host.CreateContext(RootId)).AsTask();

        // The canceled input and the later failure complete first; the combinator waits for the others.
        Assert.Equal(DurableTaskStatus.Canceled, (await ObserveAsync(inputs[1])).Status);
        Assert.Equal(DurableTaskStatus.Failed, (await ObserveAsync(inputs[3])).Status);
        releaseFirstFailure.SetResult();
        Assert.Equal(DurableTaskStatus.Failed, (await ObserveAsync(inputs[2])).Status);
        Assert.False(execution.IsCompleted);

        releaseSlow.SetResult();
        var response = await execution.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);

        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Equal("first failure", Assert.IsType<InvalidOperationException>(response.Exception).Message);
        Assert.All(inputs, static input => Assert.True(input.Response.IsCompleted));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await inputs[0].Response).Status);
        Assert.True(host.Contains(TaskId.Parse("root/$when-all-1/3")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAllIsCanceledWhenInputsWereCanceledAndNoneFailed(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ObservedDurableTask<int>[] inputs =
        [
            new(DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled input")))),
            new(DurableTask.Run(async _ =>
            {
                await releaseSlow.Task;
                return 2;
            })),
        ];
        var execution = DurableTaskRuntimeHelper.RunAsync(
            generic ? DurableTask.WhenAll<int>(inputs) : DurableTask.WhenAll((IReadOnlyList<DurableTask>)inputs),
            host.CreateContext(RootId)).AsTask();

        Assert.Equal(DurableTaskStatus.Canceled, (await ObserveAsync(inputs[0])).Status);
        Assert.False(execution.IsCompleted);
        releaseSlow.SetResult();
        var response = await execution.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("canceled input", Assert.IsAssignableFrom<OperationCanceledException>(response.Exception).Message);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await inputs[1].Response).Status);
    }

    [Fact]
    public async Task AwaitedFailureSurfacesThroughTheAwaitingExecution()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<string>(async _ =>
        {
            try
            {
                await DurableTask.WhenAll(
                [
                    DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException())),
                    DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("failed"))),
                ]);
                return DurableTaskResponse.FromResult("completed");
            }
            catch (InvalidOperationException exception)
            {
                return DurableTaskResponse.FromResult(exception.Message);
            }
        });

        Assert.Equal("failed", await await root.WithId("root").ScheduleAsync(TestCancellation));
    }

    [Fact]
    public async Task EnumerableInputsAreEnumeratedOnceWhenTheCombinatorIsCreated()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var enumerations = 0;
        IEnumerable<DurableTask<int>> Inputs()
        {
            enumerations++;
            for (var value = 1; value <= 3; value++)
            {
                var result = value;
                yield return DurableTask.Run(_ => result);
            }
        }

        var whenAll = DurableTask.WhenAll(Inputs());
        var whenAny = DurableTask.WhenAny(Inputs().Take(1));
        var untyped = DurableTask.WhenAll(Inputs().Select(static task => (DurableTask)task));
        Assert.Equal(3, enumerations);
        var root = host.CreateRootDefinition<(int[] All, (int Index, int Result) Any)>(async _ =>
        {
            await untyped;
            return DurableTaskResponse.FromResult((await whenAll, await whenAny));
        });

        var result = await await root.WithId("root").ScheduleAsync(TestCancellation);

        Assert.Equal([1, 2, 3], result.All);
        Assert.Equal((0, 1), result.Any);
        Assert.Equal(3, enumerations);
        Assert.True(host.Contains(TaskId.Parse("root/$when-all-1/2")));
        Assert.True(host.Contains(TaskId.Parse("root/$when-all-2/2")));
        Assert.True(host.Contains(TaskId.Parse("root/$when-any-3/0")));
    }

    [Fact]
    public void EnumerableOverloadsValidateTheirInputs()
    {
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAll((IEnumerable<DurableTask>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAll((IEnumerable<DurableTask<int>>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAny((IEnumerable<DurableTask>)null!));
        Assert.Throws<ArgumentNullException>(() => DurableTask.WhenAny((IEnumerable<DurableTask<int>>)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.WhenAny(Enumerable.Empty<DurableTask>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableTask.WhenAny(Enumerable.Empty<DurableTask<int>>()));
    }

    [Fact]
    public async Task ExtensionCombinatorsComposeCollectionsAndQueries()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<(int[] All, (int Index, int Result) Any, int Race, int Completed, int Each)>(async _ =>
        {
            DurableTask<int>[] inputs = [DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2)];
            var all = await inputs.WhenAll().WithId("all");
            await Enumerable.Range(0, 2).Select(static _ => (DurableTask)DurableTask.Run(static _ => { })).WhenAll().WithId("untyped");
            var any = await new[] { 3 }.Select(static value => DurableTask.Run(_ => value)).WhenAny().WithId("any");
            var race = await new List<DurableTask> { DurableTask.Run(static _ => { }) }.Race().WithId("race");
            var completed = await new[] { DurableTask.Run(static _ => 4) }.WhenAllCompleted().WithId("completed");
            var scheduled = await new[] { DurableTask.Run(static _ => 5) }.ScheduleAll().WithId("scheduled");
            await scheduled.WhenAll();
            var each = 0;
            await foreach (var handle in scheduled.WhenEach())
            {
                each += await handle;
            }

            return DurableTaskResponse.FromResult((all, any, race, await completed[0], each));
        });

        var result = await await root.WithId("root").ScheduleAsync(TestCancellation);

        Assert.Equal([1, 2], result.All);
        Assert.Equal((0, 3), result.Any);
        Assert.Equal(0, result.Race);
        Assert.Equal(4, result.Completed);
        Assert.Equal(5, result.Each);
        Assert.True(host.Contains(TaskId.Parse("root/all/1")));
        Assert.True(host.Contains(TaskId.Parse("root/untyped/1")));
        Assert.True(host.Contains(TaskId.Parse("root/any/0")));
        Assert.True(host.Contains(TaskId.Parse("root/race/0")));
        Assert.True(host.Contains(TaskId.Parse("root/completed/0")));
        Assert.True(host.Contains(TaskId.Parse("root/scheduled/0")));
    }

    [Fact]
    public void ExtensionCombinatorsValidateTheirInputs()
    {
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<DurableTask>)null!).WhenAll());
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<DurableTask<int>>)null!).WhenAll());
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<DurableTask<int>>)null!).WhenAny());
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<DurableTask<int>>)null!).ScheduleAll());
        Assert.Throws<ArgumentOutOfRangeException>(() => Enumerable.Empty<DurableTask<int>>().WhenAny());
    }

    [Fact]
    public async Task WithIdNamesTheOperationOfAnAwaitedCombinator()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<(int[] All, int Any)>(async _ =>
        {
            int[] all = await DurableTask.WhenAll([DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2)]).WithId("commit");
            var any = await DurableTask.WhenAny([DurableTask.Run(static _ => { })]).WithId("race");
            await DurableTask.WhenAll([DurableTask.Run(static _ => { })]).WithId("untyped");
            return DurableTaskResponse.FromResult((all, any));
        });

        var result = await await root.WithId("root").ScheduleAsync(TestCancellation);

        Assert.Equal([1, 2], result.All);
        Assert.Equal(0, result.Any);

        // The combinators run as part of the root, so neither they nor their combined results are stored.
        Assert.Equal(
        [
            RootId,
            TaskId.Parse("root/commit/0"),
            TaskId.Parse("root/commit/1"),
            TaskId.Parse("root/race/0"),
            TaskId.Parse("root/untyped/0"),
        ],
            host.EntryIds);
        Assert.Equal([TaskId.Parse("root/race/$winner")], host.DecisionIds);
    }

    [Fact]
    public async Task NamedOperationsKeepTheirIdentifiersWhenAnEarlierCombinatorIsAdded()
    {
        var original = await RunAsync(addEarlierCombinator: false);
        var changed = await RunAsync(addEarlierCombinator: true);

        Assert.Equal([TaskId.Parse("root/commit/0"), TaskId.Parse("root/commit/1"), TaskId.Parse("root/race/0")], original.Ids);
        Assert.Equal(original.Ids, changed.Ids.Where(static id => !id.ToString().Contains('$', StringComparison.Ordinal)));
        Assert.Equal(original.Decisions, changed.Decisions.Where(static id => !id.ToString().Contains("$when", StringComparison.Ordinal)));

        static async Task<(IReadOnlyList<TaskId> Ids, IReadOnlyList<TaskId> Decisions)> RunAsync(bool addEarlierCombinator)
        {
            var host = new TestHost(DateTimeOffset.UnixEpoch);
            var root = host.CreateRootDefinition<int>(async _ =>
            {
                if (addEarlierCombinator)
                {
                    await DurableTask.WhenAny([DurableTask.Run(static _ => { })]);
                }

                await DurableTask.WhenAll([DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2)]).WithId("commit");
                return DurableTaskResponse.FromResult(await DurableTask.WhenAny([DurableTask.Run(static _ => { })]).WithId("race"));
            });

            await await root.WithId("root").ScheduleAsync(Xunit.TestContext.Current.CancellationToken);
            return (host.EntryIds.Where(static id => id != RootId).ToArray(), host.DecisionIds);
        }
    }

    [Fact]
    public async Task ExplicitOperationNamesFollowChildNameRules()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await DurableTask.WhenAll([DurableTask.Run(static _ => { })]).WithId("$when-all-1")),
            host.CreateContext(RootId));

        Assert.StartsWith("Names can't contain '$'", Assert.IsType<ArgumentException>(response.Exception).Message, StringComparison.Ordinal);
        Assert.Empty(host.EntryIds);
    }

    [Fact]
    public async Task ScheduledNamedCombinatorRunsAsAChildTaskWithItsOwnExecution()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var root = host.CreateRootDefinition<int[]>(async _ =>
        {
            var scheduled = await DurableTask.WhenAll([DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2)])
                .WithId("batch")
                .ScheduleAsync();
            return DurableTaskResponse.FromResult(await scheduled);
        });

        var result = await await root.WithId("root").ScheduleAsync(TestCancellation);

        Assert.Equal([1, 2], result);
        Assert.Equal(
            [RootId, TaskId.Parse("root/batch"), TaskId.Parse("root/batch/$when-all-1/0"), TaskId.Parse("root/batch/$when-all-1/1")],
            host.EntryIds);
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
