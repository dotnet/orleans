using System.Collections.Concurrent;
#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Additional shared, hand-written test fakes for Phase 1 of the DurableTasks test plan.
/// Kept in a separate file from <c>TestFakes.cs</c> to avoid touching that file (which is shared across
/// concurrently-implemented phases).
/// </summary>

/// <summary>
/// Minimal concrete <see cref="IDurableTaskRequest"/> implementation which is genuinely serializable
/// (via <c>[GenerateSerializer]</c> code generation), unlike an NSubstitute-based proxy, which the Orleans
/// serializer cannot encode (there is no registered codec for a dynamically-generated proxy type). This allows
/// exercising storage code paths (e.g. <c>SetRequest</c>) which persist the request as an Orleans-serialized payload.
/// </summary>
[GenerateSerializer]
[Alias("Microsoft.Orleans.DurableTasks.Tests.TestDurableTaskRequest")]
internal sealed class TestDurableTaskRequest : IDurableTaskRequest
{
    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public InvokeMethodOptions Options { get; private set; }

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    public object? GetTarget() => null;

    public void SetTarget(ITargetHolder holder)
    {
    }

    public ValueTask<Response> Invoke() => throw new NotSupportedException("Not invokable in tests.");

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => "TestMethod";

    public string GetInterfaceName() => "Microsoft.Orleans.DurableTasks.Tests.ITestInterface";

    public string GetActivityName() => "Microsoft.Orleans.DurableTasks.Tests.ITestInterface/TestMethod";

    public MethodInfo GetMethod() => typeof(TestDurableTaskRequest).GetMethod(nameof(GetMethod))!;

    public Type GetInterfaceType() => typeof(TestDurableTaskRequest);

    public void Dispose()
    {
    }

    public DurableTask CreateTask() => DurableTask.Run(static _ => { });
}

/// <summary>A server whose polls return or throw each scripted outcome in turn, repeating the last.</summary>
internal sealed class ScriptedDurableTaskServer(params object[] outcomes) : IDurableTaskServer
{
    private int _polls;

    public int Polls => Volatile.Read(ref _polls);

    /// <summary>Gets or sets an action which runs at each poll.</summary>
    public Action? OnPoll { get; init; }

    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, IDurableTaskRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options, CancellationToken cancellationToken = default)
    {
        var poll = Interlocked.Increment(ref _polls);
        OnPoll?.Invoke();
        return outcomes[Math.Min(poll, outcomes.Length) - 1] switch
        {
            DurableTaskResponse response => new(response),
            Exception exception => ValueTask.FromException<DurableTaskResponse>(exception),
            var outcome => throw new InvalidOperationException($"Unexpected outcome '{outcome}'."),
        };
    }

    /// <summary>Gets the cancellation and abort requests which reached the server, in order.</summary>
    public ConcurrentQueue<(string Operation, string? Reason)> Requests { get; } = new();

    public ValueTask CancelAsync(TaskId taskId, CancellationToken cancellationToken = default) => Request("cancel", reason: null);

    public ValueTask CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken = default) => Request("cancel", reason);

    public ValueTask AbortAsync(TaskId taskId, string reason, CancellationToken cancellationToken = default) => Request("abort", reason);

    /// <summary>Records a request, and fails it with the script's next outcome when that outcome is an exception.</summary>
    private ValueTask Request(string operation, string? reason)
    {
        Requests.Enqueue((operation, reason));
        OnPoll?.Invoke();
        return outcomes[Math.Min(Requests.Count, outcomes.Length) - 1] is Exception exception
            ? ValueTask.FromException(exception)
            : ValueTask.CompletedTask;
    }
}

internal sealed class NotFoundDurableTaskServer(
    TaskId expectedTaskId,
    TaskId? responseTaskId = null,
    bool returnFailureResponse = false) : IDurableTaskServer
{
    private int _scheduleCallCount;
    private int _subscribeOrPollCallCount;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);
    public int SubscribeOrPollCallCount => Volatile.Read(ref _subscribeOrPollCallCount);
    public TaskId LastRequestedTaskId { get; private set; }
    public TimeSpan LastPollTimeout { get; private set; }

    public ValueTask<DurableTaskResponse> ScheduleAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return new(DurableTaskResponse.Pending);
    }

    public ValueTask<DurableTaskResponse> SubscribeOrPollAsync(
        TaskId taskId,
        SubscribeOrPollOptions options,
        CancellationToken cancellationToken = default)
    {
        if (taskId != expectedTaskId)
        {
            throw new InvalidOperationException(
                $"Expected a request for durable task '{expectedTaskId}', but received '{taskId}'.");
        }

        LastRequestedTaskId = taskId;
        LastPollTimeout = options.PollTimeout;
        Interlocked.Increment(ref _subscribeOrPollCallCount);
        var failure = new DurableTaskNotFoundException(responseTaskId ?? taskId);
        return returnFailureResponse
            ? new(DurableTaskResponse.FromException(failure))
            : ValueTask.FromException<DurableTaskResponse>(failure);
    }

    public ValueTask CancelAsync(TaskId taskId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask AbortAsync(TaskId taskId, string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
