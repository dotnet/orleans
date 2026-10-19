using System.Threading.Tasks.Sources;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Verifies that a replay which departs from its record stalls its attempt with
/// <see cref="DurableTaskAttemptCancellationReason.Diverged"/>, and the attempt rules which the abstraction owns.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskDivergenceTests
{
    private static readonly TaskId RootId = TaskId.CreateRoot("root");

    [Fact]
    public async Task RecordedSelectionWhichIsNoLongerACandidateDivergesTheAttempt()
    {
        var context = new ScriptedContext { Selection = TaskId.Parse("root/elsewhere") };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ =>
            {
                var handles = await DurableTask.ScheduleAll([DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2)]);
                try
                {
                    await ScheduledTask.WhenAny(handles, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // A divergence isn't a failure which the body can handle.
                }
            }),
            context);

        AssertDiverged<InvalidOperationException>(context, response);
    }

    [Fact]
    public async Task RecordedResultOfAnotherTypeDivergesTheAttempt()
    {
        var context = new ScriptedContext { Result = DurableTaskResponse.FromResult("one") };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ =>
            {
                try
                {
                    return await DurableTask.Run(static _ => 1);
                }
                catch (InvalidCastException)
                {
                    return -1;
                }
            }),
            context);

        AssertDiverged<InvalidCastException>(context, response);
    }

    [Fact]
    public void RecordedResultOfAnotherTypeOutsideAnExecutionThrowsTheMismatch()
    {
        var exception = Assert.Throws<InvalidCastException>(() => DurableTaskResponse.FromResult("one").GetResult<int>());

        Assert.Equal("The durable task result is 'System.String', not 'System.Int32'.", exception.Message);
    }

    [Fact]
    public async Task CompletedWaitIsReadOnce()
    {
        // A host can return a ValueTask backed by a pooled source, which reports another operation's result, or throws,
        // when it is read twice.
        var context = new ScriptedContext { SingleUseResult = DurableTaskResponse.FromResult(7) };

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(async _ => await DurableTask.Run(static _ => 0)), context);

        Assert.Equal(7, response.GetResult<int>());
    }

    [Fact]
    public async Task StartFailureSettlesTheInputsWhichStartedBeforeTheCombinatorFails()
    {
        var context = new ScriptedContext { FailingStart = "1" };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.WhenAll([DurableTask.Run(static _ => 1), DurableTask.Run(static _ => 2), DurableTask.Run(static _ => 3)]),
            context);

        // Every input which started is terminal before the start failure, the first in input order, fails the combinator.
        Assert.Equal("start 1", Assert.IsType<InvalidOperationException>(response.Exception).Message);
        Assert.Equal(["root/$when-all-1/0", "root/$when-all-1/2"], context.Canceled.Select(static id => id.ToString()).Order(StringComparer.Ordinal));
        Assert.All(context.Handles.Values, static handle => Assert.True(handle.IsTerminal));
    }

    [Fact]
    public async Task VersionCheckOfACanceledAttemptThrowsWithoutAskingTheHost()
    {
        var context = new ScriptedContext();
        context.CancelAttempt(DurableTaskAttemptCancellationReason.ActivationStopping);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => DurableTask.GetVersion("change", 0, 1) + new DurableVersion("other", 0, 1).Get()),
            context);

        var canceled = Assert.IsType<DurableTaskAttemptCanceledException>(response.Exception);
        Assert.Equal(DurableTaskAttemptCancellationReason.ActivationStopping, canceled.Reason);
        Assert.Equal(0, context.VersionChecks);
    }

    [Fact]
    public async Task HandleWaitEndsWhenTheAttemptIsCanceled()
    {
        // The abstraction links the attempt's cancellation into every handle wait, so a host need not link it itself.
        var context = new ScriptedContext { PendingStarts = true };

        var running = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ =>
            {
                var handles = await DurableTask.ScheduleAll([DurableTask.Run(static _ => 1)]);
                await ScheduledTask.WhenAll(handles, CancellationToken.None);
            }),
            context).AsTask();
        Assert.False(running.IsCompleted);
        context.CancelAttempt(DurableTaskAttemptCancellationReason.ActivationStopping);
        var response = await running.WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken);

        Assert.IsType<DurableTaskAttemptCanceledException>(response.Exception);
    }

    private static void AssertDiverged<TCause>(ScriptedContext context, DurableTaskResponse response)
        where TCause : Exception
    {
        var canceled = Assert.IsType<DurableTaskAttemptCanceledException>(response.Exception);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, canceled.Reason);
        Assert.IsType<TCause>(canceled.InnerException);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, context.AttemptCancellationReason());
        Assert.True(context.AttemptCancellationToken.IsCancellationRequested);
    }

    /// <summary>A host whose children, selections, and results each test scripts.</summary>
    private sealed class ScriptedContext() : DurableTaskContext(RootId)
    {
        public TaskId? Selection { get; init; }

        public DurableTaskResponse? Result { get; init; }

        public DurableTaskResponse? SingleUseResult { get; init; }

        public string? FailingStart { get; init; }

        public bool PendingStarts { get; init; }

        public int VersionChecks { get; private set; }

        public Dictionary<TaskId, ScriptedHandle> Handles { get; } = [];

        public List<TaskId> Canceled { get; } = [];

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public override Guid Incarnation => Guid.Empty;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken)
        {
            if (taskId.ToString().Split('/')[^1] == FailingStart)
            {
                return ValueTask.FromException<IScheduledTaskHandle>(new InvalidOperationException($"start {FailingStart}"));
            }

            var handle = new ScriptedHandle(this, taskId);
            Handles.Add(taskId, handle);
            if (!PendingStarts && FailingStart is null)
            {
                handle.Complete(Result ?? DurableTaskResponse.FromResult(0));
            }

            return new(handle);
        }

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
            => new(Selection ?? candidates[0]);

        protected override int GetVersion(string name, int minSupported, int maxSupported)
        {
            VersionChecks++;
            return maxSupported;
        }

        public void OnCanceled(TaskId taskId) => Canceled.Add(taskId);
    }

    private sealed class ScriptedHandle(ScriptedContext context, TaskId taskId) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskId TaskId => taskId;

        public bool IsTerminal => _response.Task.IsCompleted;

        public void Complete(DurableTaskResponse response) => _response.TrySetResult(response);

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            if (context.SingleUseResult is { } result)
            {
                return new(new SingleUseSource(result), token: 0);
            }

            return new(_response.Task.WaitAsync(cancellationToken));
        }

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            context.OnCanceled(taskId);
            Complete(DurableTaskResponse.Canceled);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A completed source which, like a pooled one, can be read once.</summary>
    private sealed class SingleUseSource(DurableTaskResponse response) : IValueTaskSource<DurableTaskResponse>
    {
        private int _reads;

        public DurableTaskResponse GetResult(short token)
            => ++_reads == 1 ? response : throw new InvalidOperationException("The source was read twice.");

        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => continuation(state);
    }
}
