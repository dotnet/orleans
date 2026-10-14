#nullable enable
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Hosts the durable task runtime and a grain's journaled state on one journal, so a test observes what each journal
/// write commits and recovers each activation from the committed journal only.
/// </summary>
/// <remarks>
/// Ending an activation stops its runtime without a further write, as a crash or a deactivation does, so state which
/// no write committed is lost. The next activation recovers the journal and replays the pending workflow.
/// </remarks>
internal sealed class DurableTaskReplayHarness(Func<ReplayGrain, DurableTask> workflow) : IAsyncDisposable
{
    public static readonly GrainId GrainId = GrainId.Create("replay", "one");

    private readonly DurableTaskJournalHarness _journal = new(DurableTaskJournalHarness.OrleansBinary);
    private int _effectRuns;

    /// <summary>Gets or sets the workflow which each activation runs, such as a later version after a deployment.</summary>
    public Func<ReplayGrain, DurableTask> Workflow { get; set; } = workflow;

    /// <summary>Gets how many times effects ran, across every activation.</summary>
    public int EffectRuns => Volatile.Read(ref _effectRuns);

    /// <summary>Counts one run of an effect.</summary>
    public void CountEffectRun() => Interlocked.Increment(ref _effectRuns);

    /// <summary>Creates an activation and recovers its state and pending workflows from the committed journal.</summary>
    public async Task<ReplayActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        var scope = _journal.CreateScope();
        try
        {
            var services = scope.ServiceProvider;
            var storage = services.GetRequiredService<DurableTaskGrainStorage>();
            var logicalTimes = services.GetRequiredKeyedService<IDurableDictionary<TaskId, DateTimeOffset>>("$task-logical-times");
            var counter = services.GetRequiredKeyedService<IDurableValue<int>>("counter");
            var log = services.GetRequiredKeyedService<IDurableLog<int>>("log");
            var manager = services.GetRequiredService<IJournaledStateManager>();
            var grain = new ReplayGrain(this, counter, log, manager);
            var grainContext = Substitute.For<IGrainContext>();
            grainContext.GrainId.Returns(GrainId);
            grainContext.GetTarget().Returns(grain);
            var shared = new DurableTaskGrainRuntimeShared(
                new TestGrainContextAccessor(grainContext),
                TimeProvider.System,
                NullLogger<DurableTaskGrainRuntime>.Instance,
                Options.Create(new DurableTaskOptions { ResultRetentionPeriod = TimeSpan.FromHours(1) }),
                Options.Create(new DurableMessagingOptions()),
                _journal.Serializer);
            var runtime = new DurableTaskGrainRuntime(storage, logicalTimes, shared, [new RecordingDurableTaskMessageTransport()]);
            var probe = new WriteProbe(() => (counter.Value, log.Count, CompletedTasks(storage)));
            manager.RegisterStateMachine(DurableTaskGrainRuntime.StateMachineName, runtime);
            manager.RegisterStateMachine("write-probe", probe);
            runtime.InitializeForActivation();
            await storage.ReadAsync(cancellationToken);
            await runtime.RecoverAsync(cancellationToken);
            runtime.StartRecoveredExecutions();
            return new(scope, runtime, storage, grain, probe);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => _journal.DisposeAsync();

    private static TaskId[] CompletedTasks(DurableTaskGrainStorage storage) =>
        [.. storage.Tasks.Where(static task => task.State.CompletedAt.HasValue).Select(static task => task.Id)];
}

/// <summary>One activation of the grain which <see cref="DurableTaskReplayHarness"/> hosts.</summary>
internal sealed class ReplayActivation(
    AsyncServiceScope scope,
    DurableTaskGrainRuntime runtime,
    DurableTaskGrainStorage storage,
    ReplayGrain grain,
    WriteProbe probe) : IAsyncDisposable
{
    public DurableTaskGrainRuntime Runtime { get; } = runtime;

    public DurableTaskGrainStorage Storage { get; } = storage;

    public ReplayGrain Grain { get; } = grain;

    /// <summary>Gets the grain's counter, its log's entry count, and the completed tasks which each journal write gathered.</summary>
    public IReadOnlyCollection<(int Counter, int Entries, TaskId[] Completed)> Writes => probe.Writes;

    /// <summary>Completes once a write fails after <see cref="FailWrites"/>.</summary>
    public Task FailedWrite => probe.FailedWrite;

    /// <summary>
    /// Fails every later journal write before it reaches storage, so the changes which the activation stages from now on
    /// are lost when it ends, as they are when the silo crashes before their write.
    /// </summary>
    public void FailWrites() => probe.FailWrites();

    /// <summary>Schedules the harness's workflow as a client does.</summary>
    public async Task ScheduleAsync(TaskId taskId, CancellationToken cancellationToken) =>
        await ((IDurableTaskServer)Runtime).ScheduleAsync(
            taskId,
            new ReplayWorkflowRequest
            {
                Context = new DurableTaskRequestContext { TargetId = DurableTaskReplayHarness.GrainId },
            },
            cancellationToken);

    public async Task<DurableTaskResponse> GetResponseAsync(TaskId taskId, CancellationToken cancellationToken) =>
        await ((IDurableTaskManager)Runtime).GetTask(taskId).GetResponseAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

    /// <summary>Ends the activation without a further journal write.</summary>
    public async ValueTask DisposeAsync()
    {
        await Runtime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await scope.DisposeAsync();
    }
}

/// <summary>The grain instance of one activation: its journaled counter and log, and the workflow's controls.</summary>
internal sealed class ReplayGrain(
    DurableTaskReplayHarness harness,
    IDurableValue<int> counter,
    IDurableLog<int> log,
    IJournaledStateManager manager)
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _atGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Task> _unrelatedWrites = new();

    public DurableTaskReplayHarness Harness { get; } = harness;

    /// <summary>Gets the grain's journaled state.</summary>
    public IDurableValue<int> Counter { get; } = counter;

    /// <summary>Gets the grain's journaled log, to which workflows publish their progress.</summary>
    public IDurableLog<int> Log { get; } = log;

    /// <summary>Completes once the workflow waits at the gate.</summary>
    public Task AtGate => _atGate.Task;

    /// <summary>Waits until the test opens the gate, or until the attempt is aborted.</summary>
    public Task WaitAtGateAsync()
    {
        _atGate.TrySetResult();
        return _gate.Task.WaitAsync(DurableTask.Current.CancellationToken);
    }

    public void OpenGate() => _gate.TrySetResult();

    /// <summary>Requests a journal write which commits whatever is staged when it runs, as other grain work does.</summary>
    public void RequestUnrelatedWrite() => _unrelatedWrites.Enqueue(manager.WriteStateAsync().AsTask());

    /// <summary>Waits for the unrelated writes which the workflow requested.</summary>
    public Task UnrelatedWrites => Task.WhenAll(_unrelatedWrites);

    public DurableTask CreateWorkflow() => Harness.Workflow(this);
}

/// <summary>A journal state machine which writes nothing, records what each write gathers, and can fail writes.</summary>
internal sealed class WriteProbe(Func<(int Counter, int Entries, TaskId[] Completed)> snapshot) : IStateMachine
{
    private readonly ConcurrentQueue<(int Counter, int Entries, TaskId[] Completed)> _writes = new();
    private readonly TaskCompletionSource _failedWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _failsWrites;

    public IReadOnlyCollection<(int Counter, int Entries, TaskId[] Completed)> Writes => _writes;

    /// <summary>Completes once the probe fails a write.</summary>
    public Task FailedWrite => _failedWrite.Task;

    /// <summary>Fails the preparation of every later write, so no later write reaches storage.</summary>
    public void FailWrites() => _failsWrites = true;

    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken)
    {
        if (!_failsWrites)
        {
            return ValueTask.CompletedTask;
        }

        _failedWrite.TrySetResult();
        return ValueTask.FromException(new InvalidOperationException("The write probe failed the write before it reached storage."));
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        throw new InvalidOperationException("The write probe writes no entries.");

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => _writes.Enqueue(snapshot());

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer) => _writes.Enqueue(snapshot());
}

/// <summary>A serializable request which runs the workflow of the activation which it targets.</summary>
[GenerateSerializer]
[Alias("Microsoft.Orleans.DurableTasks.Tests.ReplayWorkflowRequest")]
internal sealed class ReplayWorkflowRequest : IDurableTaskRequest
{
    [NonSerialized]
    private ReplayGrain? _grain;

    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public InvokeMethodOptions Options { get; private set; }

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    public object? GetTarget() => _grain;

    public void SetTarget(ITargetHolder holder) => _grain = (ReplayGrain?)holder.GetTarget();

    public ValueTask<Response> Invoke() => throw new NotSupportedException("Durable task requests are not invoked directly.");

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => "Run";

    public string GetInterfaceName() => "Microsoft.Orleans.DurableTasks.Tests.IReplayGrain";

    public string GetActivityName() => "Microsoft.Orleans.DurableTasks.Tests.IReplayGrain/Run";

    public MethodInfo GetMethod() => typeof(ReplayWorkflowRequest).GetMethod(nameof(GetMethod))!;

    public Type GetInterfaceType() => typeof(ReplayWorkflowRequest);

    public void Dispose()
    {
    }

    public DurableTask CreateTask() => (_grain ?? throw new InvalidOperationException("The request has no target.")).CreateWorkflow();
}
