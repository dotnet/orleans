using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableCancellationSuppressionTests
{
    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChildrenScheduledInTheScopeAreSuppressedAcrossAwaitsAndNestedScopes()
    {
        var log = new ConcurrentDictionary<string, bool>();
        var response = await RunAsync(log, async () =>
        {
            await Step("before");
            using (DurableTask.Current.SuppressCancellation())
            {
                await Step("inside");
                await Task.Yield();
                await Step("after-yield");
                await DurableTask.Delay(TimeSpan.Zero).WithId("delay");
                using (DurableTask.Current.SuppressCancellation())
                {
                    await Step("nested");
                }

                await Step("after-nested");
            }

            await Step("after");
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(
            new Dictionary<string, bool>
            {
                ["before"] = false,
                ["inside"] = true,
                ["after-yield"] = true,
                ["delay"] = true,
                ["nested"] = true,
                ["after-nested"] = true,
                ["after"] = false,
            },
            log.ToDictionary());
    }

    [Fact]
    public async Task ScopeEnteredByACalleeDoesNotLeakToItsCaller()
    {
        var log = new ConcurrentDictionary<string, bool>();
        var response = await RunAsync(log, async () =>
        {
            await CompensateAsync();
            await Step("after-callee");
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.True(log["compensation"]);
        Assert.False(log["after-callee"]);

        static async Task CompensateAsync()
        {
            using (DurableTask.Current.SuppressCancellation())
            {
                await Step("compensation");
            }
        }
    }

    [Fact]
    public async Task ScopeCoversOnlyChildrenOfTheExecutionWhichEnteredIt()
    {
        var log = new ConcurrentDictionary<string, bool>();
        var response = await RunAsync(log, async () =>
        {
            using (DurableTask.Current.SuppressCancellation())
            {
                await DurableTask.Run(async _ => await Step("grandchild")).WithId("child");
            }
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.True(log["child"]);
        Assert.False(log["grandchild"]);
    }

    [Fact]
    public async Task CanceledExecutionStillObservesItsCancellationInTheScope()
    {
        var log = new ConcurrentDictionary<string, bool>();
        var context = new RecordingContext(TaskId.CreateRoot("root"), log);
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "buyer", TestCancellation);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                using (DurableTask.Current.SuppressCancellation())
                {
                    await Step("release");
                    var current = DurableTask.Current;
                    return (cancellationToken.IsCancellationRequested, current.IsCancellationRequested, current.CancellationReason);
                }
            }),
            context);

        Assert.Equal((true, true, "buyer"), response.GetResult<(bool, bool, string?)>());
        Assert.True(log["release"]);
    }

    [Fact]
    public void ScopeRequiresADurableExecution()
    {
        Assert.Throws<InvalidOperationException>(() => DurableTask.Current.SuppressCancellation());
    }

    private static ConfiguredDurableTask Step(string name) => DurableTask.Run(static _ => { }).WithId(name);

    private static async Task<DurableTaskResponse> RunAsync(ConcurrentDictionary<string, bool> log, Func<Task> body)
        => await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await body()),
            new RecordingContext(TaskId.CreateRoot("root"), log));

    /// <summary>Records, for each child it schedules, whether the scheduling code suppressed cancellation.</summary>
    private sealed class RecordingContext(TaskId id, ConcurrentDictionary<string, bool> log) : DurableTaskContext(id)
    {
        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override async ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            CancellationToken cancellationToken)
        {
            log[taskId.ToString().Split('/')[^1]] = IsCancellationSuppressed;
            var response = await DurableTaskRuntimeHelper.RunAsync(taskDefinition, new RecordingContext(taskId, log));
            return new CompletedHandle(taskId, response);
        }

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken) => new(DurableTaskResponse.Completed);

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => throw new NotSupportedException();

        protected override ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CompletedHandle(TaskId taskId, DurableTaskResponse response) : IScheduledTaskHandle
    {
        public TaskId TaskId => taskId;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(response);

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(response);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
