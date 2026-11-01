using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Starts, observes, cancels, and signals long-running operations, independent of the protocol which exposes them.
/// </summary>
/// <remarks>
/// An operation is the root task of an operation host grain, keyed by the operation's key, which subscribes to the task
/// that performs the operation's work: a durable grain call, which the host starts as a root on its grain named by the
/// operation's key, or an existing task. Protocols, such as this package's own convention and Nexus, derive the key from
/// the request and map their requests and responses onto these operations.
/// </remarks>
internal sealed class DurableOperationService(IGrainFactory grainFactory, TimeProvider timeProvider)
{
    /// <summary>
    /// The longest single poll of a grain, and the longest wait for an operation's host to record its work. A longer wait
    /// polls repeatedly, so each poll completes well within the Orleans response timeout.
    /// </summary>
    internal static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Starts <paramref name="work"/> as the operation <paramref name="key"/>, or reaches the operation which an earlier
    /// start of the same work started, and returns its response once it is terminal or <paramref name="wait"/> elapses.
    /// </summary>
    /// <param name="key">The operation's key.</param>
    /// <param name="work">
    /// A durable grain call, which the operation runs, or a reference to an existing task, such as a
    /// <see cref="DurableTaskReference{TResult}"/>, which the operation subscribes to.
    /// </param>
    /// <param name="wait">How long to wait for the operation to complete.</param>
    /// <param name="cancellationToken">A token which abandons the start's wait.</param>
    /// <remarks>
    /// The start returns once the operation's host holds it and has recorded its work, within <see cref="MaxPollInterval"/>,
    /// so requests which then signal or cancel the operation reach its task. A start reaches an existing operation when it
    /// performs the same work: a call of the same method on the same grain, whatever its arguments, or a subscription to
    /// the same task, as the durable task runtime identifies operations.
    /// </remarks>
    /// <exception cref="DurableTaskConflictException">The operation <paramref name="key"/> performs other work.</exception>
    /// <exception cref="DurableOperationUnavailableException">The operation's host didn't answer within the request's time.</exception>
    public async Task<DurableTaskResponse> StartAsync(string key, DurableTask work, TimeSpan wait, CancellationToken cancellationToken)
    {
        var host = GetHost(key);
        var requested = DurableOperationWork.Describe(work);
        var definition = requested.IsSubscription
            ? host.SubscribeAsync(requested.Address!.Value)
            : host.RunAsync(work);

        var started = timeProvider.GetTimestamp();
        ScheduledTask<DurableTaskResponse> scheduled;
        try
        {
            scheduled = await definition.WithId(DurableOperationGrain.RootId).ScheduleAsync(cancellationToken);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            throw new DurableOperationUnavailableException(exception);
        }

        // The host's journal records the work which the operation performs, which identifies the operation. A host which
        // ended before it recorded its work, such as one which was aborted, reports its own failure.
        var recorded = await WaitForWorkAsync(host, MaxPollInterval);
        if (recorded is not null && !requested.IsSameWorkAs(recorded))
        {
            throw new DurableTaskConflictException(
                TaskId.CreateRoot(DurableOperationGrain.RootId),
                $"The operation '{key}' performs other work than the request: {recorded.Method} on {recorded.Target}.");
        }

        if (scheduled.IsCompleted)
        {
            return Unwrap(await scheduled.GetResponseAsync(cancellationToken));
        }

        if (recorded is null)
        {
            throw new DurableOperationUnavailableException();
        }

        var remaining = wait - timeProvider.GetElapsedTime(started);
        return await PollAsync(key, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// Returns the operation's response once it is terminal or <paramref name="wait"/> elapses, polling its host in polls
    /// no longer than <see cref="MaxPollInterval"/>.
    /// </summary>
    /// <exception cref="DurableTaskNotFoundException">No operation <paramref name="key"/> exists, or its retention has ended.</exception>
    /// <exception cref="DurableOperationUnavailableException">The operation's host didn't answer within the wait.</exception>
    public async Task<DurableTaskResponse> PollAsync(string key, TimeSpan wait, CancellationToken cancellationToken)
    {
        // A poll retries failures which leave the operation unaffected, such as a deactivating host, until its deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(wait + MaxPollInterval);
        var operation = GetHost(key).GetDurableTask<DurableTaskResponse>(DurableOperationGrain.RootId);
        var started = timeProvider.GetTimestamp();
        try
        {
            while (true)
            {
                var remaining = wait - timeProvider.GetElapsedTime(started);
                var isLast = remaining <= MaxPollInterval;
                var interval = !isLast ? MaxPollInterval : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                var response = await operation.GetResponseAsync(new PollingOptions { PollTimeout = interval }, deadline.Token);
                if (response.IsCompleted || isLast)
                {
                    return Unwrap(response);
                }
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DurableOperationUnavailableException(exception);
        }
    }

    /// <summary>
    /// Durably requests cancellation of a running operation, carrying <paramref name="reason"/>, and returns the response
    /// which the operation had when the request arrived.
    /// </summary>
    /// <remarks>
    /// Cancellation is cooperative. An operation which runs work cancels that work's task, whose body decides the
    /// operation's outcome. An operation which subscribes to an existing task ends its subscription and reports that it was
    /// canceled, and the task, which other parties own, continues. A terminal operation is unchanged, and a repeated request
    /// keeps the first reason.
    /// </remarks>
    /// <exception cref="DurableTaskNotFoundException">No operation <paramref name="key"/> exists, or its retention has ended.</exception>
    /// <exception cref="DurableOperationUnavailableException">The operation's host hasn't recorded its work yet.</exception>
    public async Task<DurableTaskResponse> CancelAsync(string key, string? reason, CancellationToken cancellationToken)
    {
        var host = GetHost(key);
        var response = await PollAsync(key, TimeSpan.Zero, cancellationToken);
        if (response.IsCompleted)
        {
            return response;
        }

        var work = await WaitForWorkAsync(host, MaxPollInterval) ?? throw new DurableOperationUnavailableException();
        if (work.Address is not { } address)
        {
            // The operation failed without running its work, so it is terminal.
            return await PollAsync(key, TimeSpan.Zero, cancellationToken);
        }

        try
        {
            if (work.IsSubscription)
            {
                await host.GetDurableTask<DurableTaskResponse>(DurableOperationGrain.RootId).CancelAsync(reason, cancellationToken);
            }
            else
            {
                await grainFactory.GetDurableTask<object?>(address).CancelAsync(reason, cancellationToken);
            }
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            throw new DurableOperationUnavailableException(exception);
        }

        return response;
    }

    /// <summary>Delivers <paramref name="signal"/> with <paramref name="value"/> to the operation's work, and returns how its task handled it.</summary>
    /// <exception cref="DurableTaskNotFoundException">No operation <paramref name="key"/> exists, or its retention has ended.</exception>
    /// <exception cref="DurableOperationUnavailableException">The operation's host hasn't recorded its work yet.</exception>
    public async Task<DurableSignalResult> SignalAsync<T>(string key, DurableSignal<T> signal, T value, CancellationToken cancellationToken)
    {
        // The poll establishes that the operation exists, so its host records its work. A completed operation's work
        // holds the signals which it received, so a retried signal finds its value.
        _ = await PollAsync(key, TimeSpan.Zero, cancellationToken);
        var work = await WaitForWorkAsync(GetHost(key), MaxPollInterval) ?? throw new DurableOperationUnavailableException();
        if (work.Address is not { } address)
        {
            return DurableSignalResult.TaskCompleted;
        }

        try
        {
            return await grainFactory.GetGrain(address.Target).TrySignalDurableTaskAsync(address.Id, signal, value, cancellationToken);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            throw new DurableOperationUnavailableException(exception);
        }
    }

    /// <summary>Returns the grain which hosts the operation <paramref name="key"/>.</summary>
    public GrainId GetHostId(string key) => GetHost(key).GetGrainId();

    /// <summary>
    /// Returns the response of the operation's work: the response which its host's root task returned, or the host's own
    /// response when the host failed, for example when the work's grain rejected the work, or the host was aborted.
    /// </summary>
    internal static DurableTaskResponse Unwrap(DurableTaskResponse response) =>
        response is { Status: DurableTaskStatus.CompletedSuccessfully, Result: DurableTaskResponse work } ? work : response;

    /// <summary>
    /// Returns whether <paramref name="exception"/> reports a failure to reach a grain, which leaves the operation as it
    /// was, so the client retries its request.
    /// </summary>
    internal static bool IsUnavailable(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        DurableOperationUnavailableException => true,

        // Includes SiloUnavailableException, and rejections of calls to activations which fail or move.
        OrleansMessageRejectionException or ConnectionFailedException or GatewayTooBusyException or TimeoutException => true,

        // A grain's durable task runtime rejects calls while its activation stops.
        DurableTaskAttemptCanceledException { Reason: DurableTaskAttemptCancellationReason.ActivationStopping } => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    private static async Task<DurableOperationWork?> WaitForWorkAsync(IDurableOperationGrain host, TimeSpan timeout)
    {
        try
        {
            return await host.WaitForWorkAsync(timeout);
        }
        catch (Exception exception) when (IsUnavailable(exception, CancellationToken.None))
        {
            return null;
        }
    }

    private IDurableOperationGrain GetHost(string key) => grainFactory.GetGrain<IDurableOperationGrain>(key);
}

/// <summary>
/// Represents a request which didn't reach an operation, or reached it before its host recorded its work, so it left the
/// operation as it was. The client sends the request again.
/// </summary>
internal sealed class DurableOperationUnavailableException(Exception? innerException = null)
    : Exception("The operation is unavailable. Send the request again.", innerException);
