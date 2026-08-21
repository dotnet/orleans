#nullable enable
using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Hand-written fakes used exclusively by <c>DurableTaskGrainRuntimeTests</c>.
/// Kept in a separate file from <c>TestFakes.cs</c> to avoid concurrent-edit conflicts with other test authors.
/// </summary>

/// <summary>
/// A recording fake for the internal <see cref="IDurableTaskMessageTransport"/> interface. Records every call so that
/// tests can assert on the exact arguments passed by <see cref="Orleans.DurableTasks.Runtime.DurableTaskGrainRuntime"/>.
/// </summary>
internal sealed class RecordingDurableTaskMessageTransport : IDurableTaskMessageTransport
{
    private readonly TaskCompletionSource _scheduledResume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(GrainId Sender, GrainId Target, TaskId TaskId, IDurableTaskRequest Request)> Invocations { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, DurableTaskResponse Response)> Completions { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId)> CompletionAcknowledgements { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId)> Cancellations { get; } = [];
    public List<(GrainId Target, TaskId TaskId, DateTimeOffset DueTime)> ScheduledResumes { get; } = [];
    public int CommitCount { get; private set; }
    public Task ScheduledResume => _scheduledResume.Task;
    public Action<GrainId, GrainId, TaskId, IDurableTaskRequest>? BeforeSendInvocation { get; set; }

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request)
    {
        BeforeSendInvocation?.Invoke(sender, target, taskId, request);
        Invocations.Add((sender, target, taskId, request));
    }

    public void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) => Completions.Add((sender, target, taskId, response));
    public void SendCompletionAck(GrainId sender, GrainId target, TaskId taskId) =>
        CompletionAcknowledgements.Add((sender, target, taskId));

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) => Cancellations.Add((sender, target, taskId));

    public ValueTask ScheduleResumeAsync(GrainId target, TaskId taskId, long generation, DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        ScheduledResumes.Add((target, taskId, dueTime));
        _scheduledResume.TrySetResult();
        return default;
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        CommitCount++;
        return default;
    }
}

/// <summary>
/// A hand-written, real (non-mocked) implementation of <see cref="IDurableTaskRequest"/>.
/// </summary>
/// <remarks>
/// A dynamic-proxy mock (e.g. NSubstitute's <c>Substitute.For&lt;IDurableTaskRequest&gt;()</c>) cannot be used here:
/// <see cref="Orleans.DurableTasks.Runtime.VolatileDurableTaskGrainStorage"/> stores every <see cref="Orleans.DurableTasks.Runtime.DurableTaskState"/>
/// through a real Orleans <see cref="DeepCopier{T}"/>, and Orleans' codec provider throws <c>CodecNotFoundException</c> for
/// dynamic proxy types it has never seen (confirmed empirically). A plain hand-written class paired with the trivial
/// <see cref="RuntimeTestDurableTaskRequestCopier"/> below avoids that problem entirely while still letting tests fully control
/// and observe request behavior.
/// </remarks>
internal sealed class RuntimeTestDurableTaskRequest(
    Func<DurableTask>? createTask = null,
    string interfaceName = "ITestInterface",
    string methodName = "TestMethod",
    params object?[] arguments) : IDurableTaskRequest
{
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

    public int GetArgumentCount() => arguments.Length;

    public object? GetArgument(int index) => arguments[index];

    public void SetArgument(int index, object value) => arguments[index] = value;

    public void Dispose()
    {
    }

    public string GetMethodName() => MethodName;

    public string GetInterfaceName() => InterfaceName;

    public string GetActivityName() => $"{InterfaceName}/{MethodName}";

    public Type GetInterfaceType() => typeof(object);

    public MethodInfo GetMethod() => null!;

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;
}

/// <summary>
/// Trivial reference-preserving copier for <see cref="RuntimeTestDurableTaskRequest"/>, registered so that the real Orleans
/// <see cref="DeepCopier{T}"/> used by <see cref="Orleans.DurableTasks.Runtime.VolatileDurableTaskGrainStorage"/> can copy
/// <see cref="Orleans.DurableTasks.Runtime.DurableTaskState"/> instances which reference a <see cref="RuntimeTestDurableTaskRequest"/>.
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
    Func<TaskId, IScheduledTaskHandle>? getHandle = null) : DurableTask, ISchedulableTask
{
    public int ScheduleAsyncCallCount { get; private set; }

    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ScheduleAsyncCallCount++;
        return scheduleAsync(taskId, cancellationToken);
    }

    public IScheduledTaskHandle GetHandle(TaskId taskId) => getHandle is not null
        ? getHandle(taskId)
        : throw new NotSupportedException("This test task was not configured with a GetHandle callback.");

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) => throw new NotSupportedException("This test task is not directly runnable.");
}
