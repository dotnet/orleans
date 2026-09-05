using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

/// <summary>
/// Provides operations for observing existing durable tasks on grain references.
/// </summary>
public static class DurableTaskGrainExtensions
{
    /// <summary>
    /// Gets a typed handle which observes an existing root durable task without scheduling it.
    /// </summary>
    /// <typeparam name="TResult">The durable task result type.</typeparam>
    /// <param name="target">The grain which owns the durable task.</param>
    /// <param name="rootId">The stable root task identifier.</param>
    /// <returns>A handle which can poll, wait for, or cancel the existing durable task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootId"/> is empty or whitespace.</exception>
    public static ScheduledTask<TResult> GetDurableTask<TResult>(this IAddressable target, string rootId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        return new AttachedScheduledTask<TResult>(
            TaskId.CreateRoot(rootId),
            target.AsReference<IDurableTaskGrainExtension>());
    }
}

internal sealed class AttachedScheduledTask<TResult>(
    TaskId taskId,
    IDurableTaskServer server) : ScheduledTask<TResult>
{
    private readonly GrainScheduledTaskHandle _handle =
        new(taskId, request: null, server, lastResponse: null);

    public override TaskId Id => taskId;

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
        _handle.CancelAsync(cancellationToken);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(
        PollingOptions options,
        CancellationToken cancellationToken) =>
        _handle.PollAsync(options, cancellationToken);

    protected override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) =>
        _handle.WaitAsync(cancellationToken);
}
