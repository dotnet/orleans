using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// The interface which grain call filters observe for a segment of a durable task attempt: the code of an attempt
/// between two of its durable waits, which runs as a local request on the grain activation.
/// </summary>
internal interface IDurableTaskSegment
{
    internal static readonly MethodInfo MethodInfo = typeof(IDurableTaskSegment).GetMethod(nameof(RunAsync))!;

    /// <summary>Runs the segment.</summary>
    Task RunAsync();
}

/// <summary>A local request which runs one segment of a durable task attempt.</summary>
/// <remarks>
/// The activation admits the request as it admits a grain call, so on a non-reentrant grain the segment excludes the
/// grain's other requests. The request releases the attempt's code which waits for <paramref name="gate"/>, such as the
/// body's start or the continuation of a durable wait, and completes once the attempt parks or ends.
/// </remarks>
internal sealed class DurableTaskSegment(GrainDurableTaskContext? attempt, TaskCompletionSource? gate) : ILocalRequest, IDurableTaskSegment
{
    /// <summary>
    /// Gets a segment which the activation's admission rules evaluate as any segment, and which is never received.
    /// </summary>
    public static DurableTaskSegment Probe { get; } = new(null, null);

    public ValueTask<Response> Invoke()
    {
        Debug.Assert(attempt is not null && gate is not null, "The activation receives only segments of attempts.");
        return attempt.RunSegmentAsync(gate);
    }

    // The activation rejects a queued request only once it no longer runs requests, so the attempt has ended with it.
    void ILocalRequest.Complete(Response response)
    {
    }

    Task IDurableTaskSegment.RunAsync() => throw new InvalidOperationException();

    public object? GetTarget() => this;

    public void SetTarget(ITargetHolder holder)
    {
    }

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object? value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => nameof(IDurableTaskSegment.RunAsync);

    public string GetInterfaceName() => nameof(IDurableTaskSegment);

    public string GetActivityName() => $"{nameof(IDurableTaskSegment)}/{nameof(IDurableTaskSegment.RunAsync)}";

    public MethodInfo GetMethod() => IDurableTaskSegment.MethodInfo;

    public Type GetInterfaceType() => typeof(IDurableTaskSegment);

    public TimeSpan? GetDefaultResponseTimeout() => null;

    public void Dispose()
    {
    }

    public override string ToString() => attempt is null ? "Durable task segment" : $"Durable task segment '{attempt.Id}'";
}
