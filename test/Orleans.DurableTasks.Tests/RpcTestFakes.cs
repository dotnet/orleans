#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.CodeGeneration;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Shared, hand-written test fakes for Phase 3 (DurableTaskMessageTransport, DurableTaskMessageHandler,
/// DurableTaskGrainParticipant tests). Kept in a dedicated file so as not to collide with TestFakes.cs,
/// which is shared with other concurrently-running phases.
/// </summary>
/// <remarks>
/// Naming convention: all types here are prefixed with "Rpc" to avoid collisions with fakes defined by
/// other phases operating on this same test project.
/// </remarks>

/// <summary>
/// Hand-written, wire-serializable <see cref="IDurableTaskRequest"/> implementation. Deliberately implements
/// the interface directly (not the heavy <c>DurableTaskRequest</c> abstract base) so it can be constructed
/// without a grain-reference/DI context, while remaining serializable end-to-end via <see cref="DurableEnvelopeBuilder.WithBody{T}"/>
/// thanks to <see cref="GenerateSerializerAttribute"/> plus this project's build-time code generation.
/// </summary>
[GenerateSerializer]
internal sealed class RpcTestDurableTaskRequest : IDurableTaskRequest
{
    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public int ResultValue { get; set; } = 42;

    [field: NonSerialized]
    public int CreateTaskCallCount { get; private set; }

    public DurableTask CreateTask()
    {
        CreateTaskCallCount++;
        return DurableTask.FromResult(ResultValue);
    }

    public object? GetTarget() => null;
    public void SetTarget(ITargetHolder holder) { }
    public ValueTask<Response> Invoke() => throw new NotSupportedException("Not used by these tests.");
    public int GetArgumentCount() => 0;
    public object GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));
    public void Dispose() { }
    public string GetMethodName() => "RpcTestMethod";
    public string GetInterfaceName() => "IRpcTestInterface";
    public string GetActivityName() => "IRpcTestInterface/RpcTestMethod";
    public Type GetInterfaceType() => typeof(object);
    public MethodInfo GetMethod() => null!;
    [field: NonSerialized]
    public InvokeMethodOptions Options { get; private set; }
    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;
}

/// <summary>
/// Mutable, hand-rolled <see cref="IDurableTaskState"/> used by <see cref="RpcTestDurableTaskGrainStorage"/>.
/// </summary>
internal sealed class RpcTestDurableTaskState : IDurableTaskState
{
    private readonly HashSet<GrainId> _completionDestinations = [];

    public DurableTaskResponse? Result { get; set; }
    public IReadOnlySet<GrainId> CompletionDestinations => _completionDestinations;
    public IDurableTaskRequest? Request { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    internal void AddDestination(GrainId destination) => _completionDestinations.Add(destination);
    internal void ClearDestinations() => _completionDestinations.Clear();
}

/// <summary>
/// Hand-rolled, in-memory <see cref="IDurableTaskGrainStorage"/> fake. Persists synchronously (no
/// copy-on-write semantics) to keep the construction of <see cref="DurableTaskGrainRuntime"/> simple for tests.
/// </summary>
internal sealed class RpcTestDurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private readonly Dictionary<TaskId, RpcTestDurableTaskState> _tasks = [];

    public int WriteAsyncCallCount { get; private set; }
    public int ReadAsyncCallCount { get; private set; }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks =>
        _tasks.Select(kvp => (kvp.Key, (IDurableTaskState)kvp.Value)).ToList();

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task) =>
        _tasks.Where(kvp => task.IsParentOf(kvp.Key)).Select(kvp => (kvp.Key, (IDurableTaskState)kvp.Value)).ToList();

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        if (!_tasks.TryGetValue(taskId, out var state))
        {
            state = new RpcTestDurableTaskState { Request = request, CreatedAt = DateTimeOffset.UtcNow };
            _tasks[taskId] = state;
        }

        return state;
    }

    public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request) =>
        ((RpcTestDurableTaskState)state).Request = request;

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        var s = (RpcTestDurableTaskState)state;
        s.Result = response;
        s.CompletedAt = DateTimeOffset.UtcNow;
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state) =>
        ((RpcTestDurableTaskState)state).CancellationRequestedAt = DateTimeOffset.UtcNow;

    public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination) =>
        ((RpcTestDurableTaskState)state).AddDestination(destination);

    public void ClearCompletionDestinations(TaskId taskId, IDurableTaskState state) =>
        ((RpcTestDurableTaskState)state).ClearDestinations();

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        if (_tasks.TryGetValue(taskId, out var s))
        {
            state = s;
            return true;
        }

        state = null;
        return false;
    }

    public bool RemoveTask(TaskId taskId) => _tasks.Remove(taskId);

    public void Clear() => _tasks.Clear();

    public ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        WriteAsyncCallCount++;
        return default;
    }

    public ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        ReadAsyncCallCount++;
        return default;
    }
}

/// <summary>
/// Recording fake for the internal <see cref="IDurableTaskMessageTransport"/> seam used by
/// <see cref="DurableTaskGrainRuntime"/> and by <see cref="DurableTaskMessageHandler"/> directly.
/// </summary>
internal sealed class RpcTestMessageTransport : IDurableTaskMessageTransport
{
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, IDurableTaskRequest Request)> Invocations { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, DurableTaskResponse Response)> Completions { get; } = [];
    public List<(GrainId Sender, GrainId Target, TaskId TaskId)> Cancellations { get; } = [];
    public List<(GrainId Target, TaskId TaskId, DateTimeOffset DueTime)> ScheduledResumes { get; } = [];
    public int CommitAsyncCallCount { get; private set; }

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) =>
        Invocations.Add((sender, target, taskId, request));

    public void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) =>
        Completions.Add((sender, target, taskId, response));

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) =>
        Cancellations.Add((sender, target, taskId));

    public ValueTask ScheduleResumeAsync(GrainId target, TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        ScheduledResumes.Add((target, taskId, dueTime));
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        CommitAsyncCallCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Recording fake for <see cref="IDurableOutbox"/>, used directly by <see cref="DurableTaskMessageTransport"/> tests.
/// </summary>
internal sealed class RpcTestDurableOutbox : IDurableOutbox
{
    public List<DurableEnvelope> SentEnvelopes { get; } = [];

    public int Count => SentEnvelopes.Count;

    public IEnumerable<DurableEnvelope> Messages => SentEnvelopes;

    public void Send(DurableEnvelope envelope) => SentEnvelopes.Add(envelope);

    public bool RemoveMessage(Guid messageId) => SentEnvelopes.RemoveAll(e => e.MessageId == messageId) > 0;

    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        foreach (var e in SentEnvelopes)
        {
            if (e.MessageId == messageId)
            {
                envelope = e;
                return true;
            }
        }

        envelope = default;
        return false;
    }

    public Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Recording fake for <see cref="IDurableMessageScheduler"/>, used directly by <see cref="DurableTaskMessageTransport"/> tests.
/// </summary>
internal sealed class RpcTestDurableMessageScheduler : IDurableMessageScheduler
{
    public List<(DurableEnvelope Message, DateTimeOffset DueTime)> Scheduled { get; } = [];

    public ValueTask ScheduleAsync(DurableEnvelope message, DateTimeOffset dueTime, CancellationToken cancellationToken = default)
    {
        Scheduled.Add((message, dueTime));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Recording fake for <see cref="IJournaledStateManager"/>, sufficient for verifying <see cref="DurableTaskMessageTransport.CommitAsync"/>.
/// </summary>
internal sealed class RpcTestJournaledStateManager : IJournaledStateManager
{
    public int WriteStateAsyncCallCount { get; private set; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void RegisterState(string name, IJournaledState state)
    {
    }

    public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
    {
        state = null;
        return false;
    }

    public ValueTask WriteStateAsync(CancellationToken cancellationToken)
    {
        WriteStateAsyncCallCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// Mock <see cref="IInboxHandlerContext"/>, mirroring the pattern in test/Orleans.Journaling.Tests/RoutePrefixHandlerTests.cs.
/// </summary>
internal sealed class RpcMockInboxHandlerContext(DurableEnvelope envelope, GrainId grainId) : IInboxHandlerContext
{
    public DurableEnvelope Envelope { get; } = envelope;
    public GrainId GrainId { get; } = grainId;

    public DurableEnvelopeBuilder CreateEnvelope() => throw new NotImplementedException();

    public void Send(DurableEnvelope envelope) => throw new NotImplementedException();

    public IDurableOutbox Outbox => throw new NotImplementedException();

    public void SendError(string errorCode, string message, bool isRetriable = false)
    {
        // No-op for testing.
    }

    public void SendError(Exception exception, bool isRetriable = false)
    {
        // No-op for testing.
    }
}

/// <summary>
/// Constructs real (non-mocked) <see cref="DurableTaskGrainRuntime"/> instances for Phase 3 tests, since
/// the runtime is a concrete sealed class and cannot itself be mocked/faked.
/// </summary>
internal static class RpcTestRuntimeFactory
{
    public static DurableTaskGrainRuntime Create(
        IDurableTaskGrainStorage storage,
        IGrainContext grainContext,
        IDurableTaskMessageTransport? transport = null)
    {
        var shared = new DurableTaskGrainRuntimeShared(
            new TestGrainContextAccessor(grainContext),
            TimeProvider.System,
            NullLogger<DurableTaskGrainRuntime>.Instance);
        IEnumerable<IDurableTaskMessageTransport> transports = transport is null
            ? []
            : [transport];
        return new DurableTaskGrainRuntime(storage, shared, transports);
    }
}
