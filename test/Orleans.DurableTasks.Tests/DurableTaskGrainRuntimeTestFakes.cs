#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Hand-written fakes used exclusively by <c>DurableTaskGrainRuntimeTests</c>.
/// Kept in a separate file from <c>TestFakes.cs</c> to avoid concurrent-edit conflicts with other test authors.
/// </summary>

/// <summary>
/// A recording fake for the internal <see cref="IDurableTaskMessageTransport"/> interface. Records every call so that
/// tests can assert on the exact arguments passed by <see cref="Orleans.Runtime.DurableTasks.DurableTaskGrainRuntime"/>.
/// </summary>
internal sealed class RecordingDurableTaskMessageTransport :
    IDurableTaskMessageTransport,
    IDurableTaskMessageTransaction
{
    private readonly List<(Action Commit, Action Rollback)> _participants = [];
    private readonly List<(Action Commit, Action Rollback)> _nextCommitParticipants = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, IDurableTaskRequest Request)> Invocations { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, DurableTaskResponse Response)> Completions { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId)> Cancellations { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, DurableTaskResponse Response)> CancellationAcknowledgements { get; } = [];
    public List<(GrainId Target, TaskId TaskId, DateTimeOffset DueTime)> ScheduledResumes { get; } = [];
    public int CommitCount { get; private set; }
    public bool EnlistWrites { get; set; }
    public Exception? NextCommitException { get; set; }

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) => Invocations.Add((sender, target, taskId, request));

    public void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) => Completions.Add((sender, target, taskId, response));

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) => Cancellations.Add((sender, target, taskId));

    public void SendCancellationAcknowledgement(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        DurableTaskResponse response) =>
        CancellationAcknowledgements.Add((sender, target, taskId, response));

    public ValueTask ScheduleResumeAsync(GrainId target, TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        ScheduledResumes.Add((target, taskId, dueTime));
        CompleteNextCommit(committed: true);
        return default;
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        CommitCount++;
        if (NextCommitException is { } exception)
        {
            NextCommitException = null;
            return ValueTask.FromException(exception);
        }

        return default;
    }

    public bool TryEnlist(Action commit, Action rollback)
    {
        if (!EnlistWrites)
        {
            return false;
        }

        _participants.Add((commit, rollback));
        return true;
    }

    public void EnlistNextCommit(Action commit, Action rollback) =>
        _nextCommitParticipants.Add((commit, rollback));

    public async ValueTask CommitAsync(
        Func<ValueTask> prepare,
        Action commit,
        Action rollback,
        CancellationToken cancellationToken)
    {
        try
        {
            await prepare();
            await CommitAsync(cancellationToken);
            commit();
            CompleteNextCommit(committed: true);
        }
        catch
        {
            rollback();
            CompleteNextCommit(committed: false);
            throw;
        }
    }

    public void CompleteTransaction(bool committed)
    {
        foreach (var participant in _participants)
        {
            (committed ? participant.Commit : participant.Rollback)();
        }

        _participants.Clear();
    }

    private void CompleteNextCommit(bool committed)
    {
        foreach (var participant in _nextCommitParticipants)
        {
            (committed ? participant.Commit : participant.Rollback)();
        }

        _nextCommitParticipants.Clear();
    }
}

/// <summary>
/// A hand-written, real (non-mocked) implementation of <see cref="IDurableTaskRequest"/>.
/// </summary>
/// <remarks>
/// A dynamic-proxy mock (e.g. NSubstitute's <c>Substitute.For&lt;IDurableTaskRequest&gt;()</c>) cannot be used here:
/// <see cref="Orleans.Runtime.DurableTasks.VolatileDurableTaskGrainStorage"/> stores every <see cref="Orleans.Runtime.DurableTasks.DurableTaskState"/>
/// through a real Orleans <see cref="DeepCopier{T}"/>, and Orleans' codec provider throws <c>CodecNotFoundException</c> for
/// dynamic proxy types it has never seen (confirmed empirically). A plain hand-written class paired with the trivial
/// <see cref="RuntimeTestDurableTaskRequestCopier"/> below avoids that problem entirely while still letting tests fully control
/// and observe request behavior.
/// </remarks>
internal sealed class RuntimeTestDurableTaskRequest(
    Func<DurableTask>? createTask = null,
    string interfaceName = "ITestInterface",
    string methodName = "TestMethod",
    object?[]? arguments = null) : IDurableTaskRequest
{
    private readonly object?[] _arguments = arguments ?? [];
    public DurableTaskRequestContext? Context { get; set; }

    public string InterfaceName { get; set; } = interfaceName;

    public string MethodName { get; set; } = methodName;

    public int CreateTaskCallCount { get; private set; }

    public List<object?> SetTargetCalls { get; } = [];

    public InvokeMethodOptions Options { get; private set; }

    public DurableTask CreateTask()
    {
        CreateTaskCallCount++;
        return createTask?.Invoke() ?? DurableTask.FromResult(0);
    }

    public object? GetTarget() => null;

    public void SetTarget(ITargetHolder holder) => SetTargetCalls.Add(holder);

    public ValueTask<Response> Invoke() => throw new NotSupportedException("Durable task requests can not be invoked directly.");

    public int GetArgumentCount() => _arguments.Length;

    public object GetArgument(int index) => _arguments[index]!;

    public void SetArgument(int index, object value) => _arguments[index] = value;

    public void Dispose()
    {
    }

    public string GetMethodName() => MethodName;

    public string GetInterfaceName() => InterfaceName;

    public string GetActivityName() => $"{InterfaceName}/{MethodName}";

    public Type GetInterfaceType() => typeof(object);

    public MethodInfo GetMethod() =>
        typeof(RuntimeTestDurableTaskRequest).GetMethod(
            nameof(TestMethodSignature),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    private static void TestMethodSignature()
    {
    }
}

[GenerateSerializer]
internal sealed record RuntimeTestComplexArgument
{
    [Id(0)]
    public string Name { get; set; } = "";

    [Id(1)]
    public int[] Values { get; set; } = [];
}

/// <summary>
/// Trivial reference-preserving copier for <see cref="RuntimeTestDurableTaskRequest"/>, registered so that the real Orleans
/// <see cref="DeepCopier{T}"/> used by <see cref="Orleans.Runtime.DurableTasks.VolatileDurableTaskGrainStorage"/> can copy
/// <see cref="Orleans.Runtime.DurableTasks.DurableTaskState"/> instances which reference a <see cref="RuntimeTestDurableTaskRequest"/>.
/// </summary>
[RegisterCopier]
internal sealed class RuntimeTestDurableTaskRequestCopier : IDeepCopier<RuntimeTestDurableTaskRequest>
{
    public RuntimeTestDurableTaskRequest DeepCopy(RuntimeTestDurableTaskRequest input, CopyContext context) => input;
}

/// <summary>
/// A hand-written <see cref="DurableTask"/> which also implements <see cref="ISchedulableTask"/>, allowing tests to fully
/// control the "schedulable task" branch of <c>DurableTaskGrainRuntime.ScheduleChildAsync</c> without depending on ambient
/// <see cref="DurableExecutionContext.CurrentContext"/> state (unlike, e.g., <c>DurableTask.Delay</c>).
/// </summary>
internal sealed class TestSchedulableTask(
    Func<TaskId, CancellationToken, ValueTask<DurableTaskResponse>> scheduleAsync,
    Func<TaskId, IScheduledTaskHandle>? getHandle = null,
    bool commitsDurableState = false) : DurableTask, ISchedulableTask
{
    public int ScheduleAsyncCallCount { get; private set; }
    public bool CommitsDurableState { get; } = commitsDurableState;

    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ScheduleAsyncCallCount++;
        return scheduleAsync(taskId, cancellationToken);
    }

    public IScheduledTaskHandle GetHandle(TaskId taskId) => getHandle is not null
        ? getHandle(taskId)
        : throw new NotSupportedException("This test task was not configured with a GetHandle callback.");

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) => throw new NotSupportedException("This test task is not directly runnable.");
}
