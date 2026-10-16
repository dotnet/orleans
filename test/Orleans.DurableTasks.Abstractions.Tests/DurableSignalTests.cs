using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public partial class DurableSignalTests
{
    private static readonly DurableSignal<Approval> Approved = new("approved");
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UnixEpoch.AddHours(1);

    [Fact]
    public void DescriptorValidatesNamesAndDerivesOccurrenceNames()
    {
        Assert.Throws<ArgumentNullException>(() => new DurableSignal<int>(null!));
        Assert.Throws<ArgumentException>(() => new DurableSignal<int>(string.Empty));
        Assert.Throws<ArgumentNullException>(() => Approved.For(null!));
        Assert.Throws<ArgumentException>(() => Approved.For(string.Empty));

        var round = Approved.For("round-2");

        Assert.Equal("approved", Approved.Name);
        Assert.Equal("approved/round-2", round.Name);
        Assert.Equal("approved/round-2", round.ToString());
        Assert.Equal("approved/round-2/line-1", round.For("line-1").Name);
    }

    [Fact]
    public async Task AwaitedWaitRunsInTheAwaitingExecutionAndReturnsTheValue()
    {
        var host = new SignalHost();
        host.Send("approved", DurableTaskResponse.FromResult(new Approval("ada")));
        var context = new SignalContext(host, TaskId.CreateRoot("workflow"));

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => (await Approved.WaitAsync()).Approver),
            context);

        Assert.Equal("ada", response.GetResult<string>());
        var wait = Assert.Single(host.Waits);
        Assert.Equal((TaskId.CreateRoot("workflow"), "approved"), (wait.ExecutionId, wait.Name));
        Assert.Equal(context.CancellationToken, wait.CancellationToken);

        // The wait runs inline, so it schedules no child.
        Assert.Empty(host.Children);
    }

    [Fact]
    public async Task WaitBeginsBeforeTheSignalAndCompletesWhenItArrives()
    {
        var host = new SignalHost();
        var running = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.For("2").WaitAsync()),
            new SignalContext(host, TaskId.CreateRoot("workflow"))).AsTask();
        await host.WaitStarted("approved/2");
        Assert.False(running.IsCompleted);

        host.Send("approved/2", DurableTaskResponse.FromResult(new Approval("grace")));

        Assert.Equal(new Approval("grace"), (await running).GetResult<Approval>());
    }

    [Fact]
    public async Task SignalWinsWhenAnyRaceWithTimeoutAndTheWinnerIsReadAgain()
    {
        var host = new SignalHost();
        var delay = new TaskCompletionSource<DurableTaskResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Delay = (_, _) => new(delay.Task);
        var running = DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(async _ =>
        {
            var index = await DurableTask.WhenAny([Approved.WaitAsync(), DurableTask.DelayUntil(Deadline)]);
            return index == 0 ? (await Approved.WaitAsync()).Approver : "timed out";
        }), new SignalContext(host, TaskId.CreateRoot("workflow"))).AsTask();
        await host.WaitStarted("approved");

        host.Send("approved", DurableTaskResponse.FromResult(new Approval("ada")));

        Assert.Equal("ada", (await running).GetResult<string>());
        Assert.Equal(Deadline, host.LastDueTime);

        // The race schedules the wait in an indexed slot, whose execution waits for the signal; the read after the race
        // runs inline in the workflow.
        Assert.Equal(
            [(TaskId.Parse("workflow/$when-any-1/0"), "approved"), (TaskId.CreateRoot("workflow"), "approved")],
            host.Waits.Select(static wait => (wait.ExecutionId, wait.Name)));
        Assert.Equal(
            [TaskId.Parse("workflow/$when-any-1/0"), TaskId.Parse("workflow/$when-any-1/1")],
            host.Children.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
        delay.SetResult(DurableTaskResponse.Completed);
    }

    [Fact]
    public async Task TimeoutWinsWhenAnyRaceWhenTheSignalDoesNotArrive()
    {
        var host = new SignalHost { Delay = static (_, _) => new(DurableTaskResponse.Completed) };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await DurableTask.WhenAny([Approved.WaitAsync(), DurableTask.DelayUntil(Deadline)])),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.Equal(1, response.GetResult<int>());
        Assert.Equal(Deadline, host.LastDueTime);
    }

    [Fact]
    public async Task DurableCancellationCancelsTheWait()
    {
        var host = new SignalHost();
        var context = new SignalContext(host, TaskId.CreateRoot("workflow"));
        var running = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.WaitAsync()),
            context).AsTask();
        await host.WaitStarted("approved");

        DurableTaskRuntimeHelper.RequestTaskCancellation(context);

        Assert.Equal(DurableTaskStatus.Canceled, (await running).Status);
    }

    [Fact]
    public async Task AttemptCancellationEndsAPendingWaitWithTheAttemptsCancellation()
    {
        var host = new SignalHost();
        var context = new SignalContext(host, TaskId.CreateRoot("workflow"));
        var running = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.WaitAsync()),
            context).AsTask();
        await host.WaitStarted("approved");

        DurableTaskRuntimeHelper.CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);

        var response = await running;
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        var exception = Assert.IsType<DurableTaskAttemptCanceledException>(response.Exception);
        Assert.Equal(DurableTaskAttemptCancellationReason.ActivationStopping, exception.Reason);
        Assert.Equal(context.AttemptCancellationToken, exception.CancellationToken);
        Assert.Equal(context.CancellationToken, Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException).CancellationToken);
        Assert.False(context.IsTaskCancellationRequested);
    }

    [Fact]
    public async Task ValueOfAnotherTypeFailsTheWait()
    {
        var host = new SignalHost();
        host.Send("approved", DurableTaskResponse.FromResult(42));

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.WaitAsync()),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.IsType<InvalidCastException>(response.Exception);
    }

    [Fact]
    public async Task WaitRequiresHostSupport()
    {
        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.WaitAsync()),
            new SignalContext(new SignalHost(), TaskId.CreateRoot("workflow"), supportsSignals: false));

        Assert.IsType<NotSupportedException>(response.Exception);
    }

    [Fact]
    public async Task WaitRequiresADurableExecution()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Approved.WaitAsync());
    }

    private sealed record Approval(string Approver);

    /// <summary>A host which latches signal values by name and runs scheduled children in child contexts.</summary>
    private sealed class SignalHost
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<DurableTaskResponse>> _signals = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waitStarted = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<TaskId, Child> _children = new();

        public ConcurrentQueue<(TaskId ExecutionId, string Name, CancellationToken CancellationToken)> Waits { get; } = new();

        public IEnumerable<TaskId> Children => _children.Keys;

        public Func<DateTimeOffset, CancellationToken, ValueTask<DurableTaskResponse>> Delay { get; set; } =
            static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DurableTaskResponse.Completed;
            };

        public DateTimeOffset? LastDueTime { get; private set; }

        public void Send(string name, DurableTaskResponse value) => GetSignal(name).TrySetResult(value);

        public Task WaitStarted(string name) =>
            _waitStarted.GetOrAdd(name, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                .WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        public ValueTask<DurableTaskResponse> WaitForSignalAsync(TaskId executionId, string name, CancellationToken cancellationToken)
        {
            Waits.Enqueue((executionId, name, cancellationToken));
            _waitStarted.GetOrAdd(name, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return new(GetSignal(name).Task.WaitAsync(cancellationToken));
        }

        public ValueTask<DurableTaskResponse> ScheduleDelayAsync(DateTimeOffset dueTime, CancellationToken cancellationToken)
        {
            LastDueTime = dueTime;
            return Delay(dueTime, cancellationToken);
        }

        public IScheduledTaskHandle Schedule(TaskId taskId, DurableTask definition) =>
            _children.GetOrAdd(taskId, id => new(id, definition, new SignalContext(this, id)));

        public IScheduledTaskHandle GetHandle(TaskId taskId) => _children[taskId];

        /// <summary>Gets the child which the host scheduled under <paramref name="taskId"/>.</summary>
        public Child GetChild(TaskId taskId) => _children[taskId];

        private TaskCompletionSource<DurableTaskResponse> GetSignal(string name) =>
            _signals.GetOrAdd(name, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>A child execution, whose cancellation requests reach its context as a host's do.</summary>
        public sealed class Child(TaskId taskId, DurableTask definition, SignalContext context) : IScheduledTaskHandle
        {
            public TaskId TaskId => taskId;

            /// <summary>Gets the child's terminal response, once it ends.</summary>
            private Task<DurableTaskResponse> Response { get; } = DurableTaskRuntimeHelper.RunAsync(definition, context).AsTask();

            /// <summary>Gets the child's terminal status, or <see langword="null"/> while it runs.</summary>
            public DurableTaskStatus? Status => Response.IsCompletedSuccessfully ? Response.Result.Status : null;

            /// <summary>Gets the reason of the first cancellation request.</summary>
            public string? CancellationReason { get; private set; }

            public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
                new(Response.WaitAsync(cancellationToken));

            public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) =>
                new(Response.IsCompletedSuccessfully ? Response.Result : DurableTaskResponse.Pending);

            public ValueTask CancelAsync(CancellationToken cancellationToken) => CancelAsync(reason: null, cancellationToken);

            public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken)
            {
                CancellationReason ??= reason;
                DurableTaskRuntimeHelper.RequestTaskCancellation(context, reason);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SignalContext(SignalHost host, TaskId taskId, bool supportsSignals = true) : DurableTaskContext(taskId)
    {
        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override ValueTask<DurableTaskResponse> WaitForSignalAsync(string name, CancellationToken cancellationToken) =>
            supportsSignals
                ? host.WaitForSignalAsync(Id, name, cancellationToken)
                : base.WaitForSignalAsync(name, cancellationToken);

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken) => new(host.Schedule(taskId, taskDefinition));

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken) => host.ScheduleDelayAsync(dueTime, cancellationToken);

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => host.GetHandle(taskId);

        protected override async ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken)
        {
            var waits = candidates.Select(id => host.GetHandle(id).WaitAsync(cancellationToken).AsTask()).ToArray();
            return candidates[Array.IndexOf(waits, await Task.WhenAny(waits))];
        }
    }
}
