#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Messaging;
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
    private readonly TaskCompletionSource _resumeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<HierarchicalKey, (GrainId Sender, GrainId Target, TaskId TaskId)> _completionIds = [];

    public List<(GrainId Sender, GrainId Target, TaskId TaskId, IDurableTaskRequest Request)> Invocations { get; } = [];
    public List<RecordedCompletion> Completions { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId)> Cancellations { get; } = [];
    public List<(GrainId GrainId, TaskId TaskId, long Generation, DateTimeOffset DueTime)> Resumes { get; } = [];
    public Task ResumeSent => _resumeSent.Task;
    public Action<GrainId, GrainId, TaskId, IDurableTaskRequest>? BeforeSendInvocation { get; set; }
    public Action<GrainId, GrainId, TaskId>? BeforeSendCancellation { get; set; }

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request)
    {
        BeforeSendInvocation?.Invoke(sender, target, taskId, request);
        Invocations.Add((sender, target, taskId, request));
    }

    public HierarchicalKey GetCompletionMessageId(GrainId sender, GrainId target, TaskId taskId)
    {
        var messageId = DurableTaskMessageTransport.CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.CompletionSubject);
        _completionIds[messageId] = (sender, target, taskId);
        return messageId;
    }

    public void SendCompletion(HierarchicalKey messageId)
    {
        var (sender, target, taskId) = _completionIds[messageId];
        Completions.Add(new(sender, target, taskId, messageId));
    }

    public OutboxMessage CreateCompletionMessage(HierarchicalKey messageId, GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) =>
        MessagingTestHelpers.MessageTypes.Completion.Create(
            messageId,
            target,
            new DurableTaskCompletionMessage { TaskId = taskId, Response = response, Sender = sender });

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId)
    {
        BeforeSendCancellation?.Invoke(sender, target, taskId);
        Cancellations.Add((sender, target, taskId));
    }

    public void SendResume(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime)
    {
        Resumes.Add((grainId, taskId, generation, dueTime));
        _resumeSent.TrySetResult();
    }
}

/// <summary>A completion the runtime sent as a reference to its stored result.</summary>
internal sealed record RecordedCompletion(GrainId Sender, GrainId Target, TaskId TaskId, HierarchicalKey MessageId)
{
    /// <summary>Materializes the completion from the runtime, as the outbox does for a delivery attempt.</summary>
    public DurableTaskCompletionMessage Materialize(DurableTaskGrainRuntime runtime)
    {
        Xunit.Assert.True(((IOutboxMessageSource)runtime).TryGetMessage(MessageId, out var message));
        Xunit.Assert.Equal(MessageId, message.Envelope.MessageId);
        Xunit.Assert.Equal(Target, message.ReceiverId);
        return MessagingTestHelpers.MessageTypes.Completion.Decode(message);
    }
}

/// <summary>
/// A hand-written, real (non-mocked) implementation of <see cref="IDurableTaskRequest"/>.
/// </summary>
/// <remarks>
/// A dynamic-proxy mock (e.g. NSubstitute's <c>Substitute.For&lt;IDurableTaskRequest&gt;()</c>) cannot be copied by a real
/// Orleans <see cref="DeepCopier{T}"/>: Orleans' codec provider throws <c>CodecNotFoundException</c> for
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
/// Trivial reference-preserving copier for <see cref="RuntimeTestDurableTaskRequest"/>, registered so that a real Orleans
/// <see cref="DeepCopier{T}"/> can copy values which reference a <see cref="RuntimeTestDurableTaskRequest"/>.
/// </summary>
[RegisterCopier]
internal sealed class RuntimeTestDurableTaskRequestCopier : IDeepCopier<RuntimeTestDurableTaskRequest>
{
    public RuntimeTestDurableTaskRequest? DeepCopy(RuntimeTestDurableTaskRequest? input, CopyContext context) => input;
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
        : new PendingScheduledTaskHandle(taskId);

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) => throw new NotSupportedException("This test task is not directly runnable.");

    private sealed class PendingScheduledTaskHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public TaskId TaskId { get; } = taskId;

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) =>
            new(DurableTaskResponse.Pending);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => default;
    }
}
