using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Starts, observes, cancels, and signals long-running operations, independent of the protocol which exposes them.
/// </summary>
/// <remarks>
/// An operation is the root task of an operation host grain, keyed by the operation's key, which subscribes to the task
/// that performs the operation's work: a durable grain call, which the host starts as a root on its grain named by the
/// operation's key, or an existing task. Protocols, such as this package's own convention, derive the key from the request and map their requests and responses onto these operations.
/// </remarks>
internal sealed class DurableOperationService(IGrainFactory grainFactory, TimeProvider timeProvider)
{
    /// <summary>
    /// The longest single poll of a grain. A longer wait polls repeatedly, so each poll completes well within the Orleans
    /// response timeout.
    /// </summary>
    internal static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(10);

    private static readonly TaskId RootTaskId = TaskId.CreateRoot(DurableOperationGrain.RootId);

    /// <summary>
    /// Starts <paramref name="work"/> as the operation <paramref name="key"/>, or reaches the operation which an earlier
    /// start of the same work started, and returns its response once it is terminal or <paramref name="wait"/> elapses.
    /// </summary>
    /// <param name="key">The operation's key.</param>
    /// <param name="work">
    /// A durable grain call, which the operation runs, or a <see cref="DurableTaskReference{TResult}"/> to an existing
    /// task, which the operation subscribes to.
    /// </param>
    /// <param name="wait">How long to wait for the operation to complete.</param>
    /// <param name="cancellationToken">A token which abandons the start's wait.</param>
    /// <remarks>
    /// The start returns once the grain which performs the work holds its task, so requests which then signal or cancel
    /// the operation reach it.
    /// </remarks>
    /// <exception cref="DurableTaskConflictException">The operation <paramref name="key"/> performs other work.</exception>
    public async Task<DurableTaskResponse> StartAsync(string key, DurableTask work, TimeSpan wait, CancellationToken cancellationToken)
    {
        var host = GetHost(key);
        DurableTask<DurableTaskResponse> definition;
        if (work is IDurableTaskRequest)
        {
            definition = host.RunAsync(work);
        }
        else if (work is IDurableTaskReference reference)
        {
            definition = host.SubscribeAsync(reference.Target, reference.Id);
        }
        else
        {
            throw new InvalidOperationException(
                $"A durable operation runs a durable grain call, such as 'grain.MethodAsync(...)', or subscribes to an existing task, which 'grain.GetDurableTask<T>(id)' references, not '{work.GetType()}'.");
        }

        var scheduled = await definition.WithId(DurableOperationGrain.RootId).ScheduleAsync(cancellationToken);
        if (scheduled.IsCompleted)
        {
            return Unwrap(await scheduled.GetResponseAsync(cancellationToken));
        }

        _ = await host.GetTargetAsync(isCompleted: false, cancellationToken);
        return await PollAsync(key, wait, cancellationToken);
    }

    /// <summary>
    /// Returns the operation's response once it is terminal or <paramref name="wait"/> elapses, polling its host in polls
    /// no longer than <see cref="MaxPollInterval"/>.
    /// </summary>
    /// <exception cref="DurableTaskNotFoundException">No operation <paramref name="key"/> exists, or its retention has ended.</exception>
    public async Task<DurableTaskResponse> PollAsync(string key, TimeSpan wait, CancellationToken cancellationToken)
    {
        var server = GetHost(key).AsReference<IDurableTaskGrainExtension>();
        var started = timeProvider.GetTimestamp();
        while (true)
        {
            var remaining = wait - timeProvider.GetElapsedTime(started);
            var isLast = remaining <= MaxPollInterval;
            var interval = !isLast ? MaxPollInterval : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            var response = await server.SubscribeOrPollAsync(RootTaskId, new SubscribeOrPollOptions { PollTimeout = interval }, cancellationToken);
            if (response.IsCompleted || isLast)
            {
                return Unwrap(response);
            }
        }
    }

    /// <summary>
    /// Durably requests cancellation of a running operation, carrying <paramref name="reason"/>, and returns the response
    /// which the operation had when the request arrived.
    /// </summary>
    /// <remarks>
    /// Cancellation is cooperative: the work's body decides the operation's outcome. The request cancels the task which
    /// performs the work, the call which the operation started or the existing task which it subscribes to. A terminal
    /// operation is unchanged, and a repeated request keeps the first reason.
    /// </remarks>
    /// <exception cref="DurableTaskNotFoundException">No operation <paramref name="key"/> exists, or its retention has ended.</exception>
    public async Task<DurableTaskResponse> CancelAsync(string key, string? reason, CancellationToken cancellationToken)
    {
        var response = await PollAsync(key, TimeSpan.Zero, cancellationToken);
        if (!response.IsCompleted && await GetHost(key).GetTargetAsync(isCompleted: false, cancellationToken) is { } target)
        {
            await grainFactory.GetGrain<IDurableTaskGrainExtension>(target.Grain).CancelAsync(target.TaskId, reason, cancellationToken);
        }

        return response;
    }

    /// <summary>Delivers <paramref name="signal"/> with <paramref name="value"/> to the operation's work, and returns how its task handled it.</summary>
    /// <exception cref="DurableTaskNotFoundException">No operation <paramref name="key"/> exists, or its retention has ended.</exception>
    public async Task<DurableSignalResult> SignalAsync<T>(string key, DurableSignal<T> signal, T value, CancellationToken cancellationToken)
    {
        // The poll establishes that the operation exists, so its host has a target or will record one.
        var response = await PollAsync(key, TimeSpan.Zero, cancellationToken);
        return await GetHost(key).GetTargetAsync(response.IsCompleted, cancellationToken) is { } target
            ? await grainFactory.GetGrain(target.Grain).TrySignalDurableTaskAsync(target.TaskId, signal, value, cancellationToken)
            : response.IsCompleted ? DurableSignalResult.TaskCompleted : DurableSignalResult.TaskNotFound;
    }

    /// <summary>Returns the grain which hosts the operation <paramref name="key"/>.</summary>
    public GrainId GetHostId(string key) => GetHost(key).GetGrainId();

    /// <summary>Returns the grain which a durable grain call targets.</summary>
    public static GrainId GetTarget(DurableTask call) => call is IDurableTaskRequest { Context.TargetId: var grainId }
        ? grainId
        : throw new InvalidOperationException(
            $"A durable operation runs a durable grain call, such as 'grain.MethodAsync(...)', not '{call.GetType()}'.");

    /// <summary>
    /// Returns the response of the operation's work: the response which its host's root task returned, or the host's own
    /// response when the host failed, for example when it was aborted, or ended before its work began.
    /// </summary>
    internal static DurableTaskResponse Unwrap(DurableTaskResponse response) =>
        response is { Status: DurableTaskStatus.CompletedSuccessfully, Result: DurableTaskResponse work } ? work : response;

    private IDurableOperationGrain GetHost(string key) => grainFactory.GetGrain<IDurableOperationGrain>(key);

}
