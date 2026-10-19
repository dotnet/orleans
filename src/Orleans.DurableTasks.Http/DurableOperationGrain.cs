using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Hosts one HTTP operation: a root task which subscribes to the task that performs the operation's work, and completes
/// with that task's response.
/// </summary>
/// <remarks>
/// <para>
/// The grain's key is the operation's key, which derives from its scope, its caller, and its identifier, so every request
/// which names the operation reaches this grain without knowing which grain runs the operation's work. The root task's
/// result is the work's terminal response, including a failure or cancellation, so the operation reports it exactly.
/// </para>
/// <para>
/// The work is a task on its grain: a durable grain call, which the host starts as a detached root named by the
/// operation's key, or an existing task, which a reference names. The host's journal records the work once its grain
/// holds the task, and is the only record of it: requests which signal or cancel the operation read the work from the
/// journal and address its task directly.
/// </para>
/// </remarks>
[Alias("Orleans.DurableTasks.Http.IDurableOperationGrain")]
internal interface IDurableOperationGrain : IGrainWithStringKey
{
    /// <summary>Runs <paramref name="call"/> as the operation's work, and returns its terminal response.</summary>
    /// <param name="call">
    /// A generated durable grain-call request, which the call serializes by its own type. Requests are immutable once
    /// created, so the call passes it without a copy.
    /// </param>
    [Alias("Run")]
    DurableTask<DurableTaskResponse> RunAsync([Immutable] DurableTask call);

    /// <summary>Subscribes to the existing task at <paramref name="address"/>, and returns its terminal response.</summary>
    [Alias("Subscribe")]
    DurableTask<DurableTaskResponse> SubscribeAsync(DurableTaskAddress address);

    /// <summary>
    /// Returns the operation's work once the host's journal durably records it, or <see langword="null"/> when it doesn't
    /// within <paramref name="timeout"/>, or the activation stops first.
    /// </summary>
    /// <remarks>
    /// The journal records the work once its grain holds the task, so a request which then signals or cancels the work
    /// reaches it. An operation whose work's grain rejected the work records <see cref="DurableOperationWork.NotStarted"/>.
    /// </remarks>
    [Alias("WaitForWork"), Orleans.Concurrency.AlwaysInterleave]
    Task<DurableOperationWork?> WaitForWorkAsync(TimeSpan timeout);
}

/// <summary>Identifies the task which performs an operation's work.</summary>
/// <param name="Address">The task's address, or <see langword="null"/> for work which never started.</param>
/// <param name="IsSubscription">
/// Whether the operation subscribes to an existing task, which other parties own, rather than running work of its own.
/// </param>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableOperationWork")]
internal sealed record DurableOperationWork(
    [property: Id(0)] DurableTaskAddress? Address,
    [property: Id(1)] bool IsSubscription)
{
    /// <summary>The work of an operation whose work's grain didn't accept the work, which therefore never ran.</summary>
    public static DurableOperationWork NotStarted { get; } = new(Address: null, IsSubscription: false);

    private const string NotStartedText = "-";

    /// <summary>
    /// Formats the work as <c>{kind}{grain length}:{grain}{task}</c>, where the kind is <c>r</c> for work which the
    /// operation runs and <c>s</c> for a subscription, which <see cref="Parse"/> reads.
    /// </summary>
    /// <remarks>
    /// The journal stores the text, which every journal format serializes without application configuration.
    /// <see cref="NotStarted"/> is <c>-</c>.
    /// </remarks>
    public string Format()
    {
        if (Address is not { } address)
        {
            return NotStartedText;
        }

        var grain = address.Target.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{(IsSubscription ? 's' : 'r')}{grain.Length}:{grain}{address.Id}");
    }

    public static DurableOperationWork Parse(string value)
    {
        if (value == NotStartedText)
        {
            return NotStarted;
        }

        var separator = value.IndexOf(':');
        var grainLength = int.Parse(value.AsSpan(1, separator - 1), CultureInfo.InvariantCulture);
        return new(
            new(GrainId.Parse(value.Substring(separator + 1, grainLength)), TaskId.Parse(value[(separator + 1 + grainLength)..])),
            IsSubscription: value[0] == 's');
    }
}

[GrainType("orleans-durable-operation")]
internal sealed class DurableOperationGrain(
    [FromKeyedServices("work")] IDurableValue<string?> work,
    IGrainFactory grainFactory) : DurableGrain, IDurableOperationGrain
{
    /// <summary>The root identifier of the operation's task on its host.</summary>
    public const string RootId = "operation";

    /// <summary>The identifier segment of the host's subscription to the operation's work.</summary>
    private const string SubscriptionId = "work";

    public async DurableTask<DurableTaskResponse> RunAsync(DurableTask call)
    {
        // The call runs as a root on its grain, named by the operation's key, which is unique among the operations that
        // call the grain. Scheduling returns once the grain holds it, so the work which the journal then records is
        // reachable by the requests which signal or cancel it.
        ScheduledTask root;
        try
        {
            root = await call.Detached().WithId(this.GetPrimaryKeyString()).ScheduleAsync();
        }
        catch (Exception exception) when (exception is DurableTaskRejectedException or DurableTaskConflictException or DurableTaskDeliveryException)
        {
            // The journal records that the work never started with the operation's failure, so requests which wait for
            // the work end.
            work.Value = DurableOperationWork.NotStarted.Format();
            throw new DurableOperationNotStartedException(
                exception switch
                {
                    DurableTaskRejectedException rejected => rejected.Code,
                    DurableTaskConflictException => DurableTaskRejectionCodes.Conflict,
                    _ => null,
                },
                exception);
        }

        var recorded = root.TryGetAddress(out var address);
        Debug.Assert(recorded, "A detached root has an address.");
        return await SubscribeCoreAsync(address, isSubscription: false);
    }

    public async DurableTask<DurableTaskResponse> SubscribeAsync(DurableTaskAddress address) =>
        await SubscribeCoreAsync(address, isSubscription: true);

    public async Task<DurableOperationWork?> WaitForWorkAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            // The committed watch delivers the journal's value once its write is acknowledged, and ends when the
            // activation stops, so the answer is always durable.
            await foreach (var change in work.WatchAsync(DurableWatchMode.Committed, cancellation.Token))
            {
                if (change.Value is { } value)
                {
                    return DurableOperationWork.Parse(value);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        return null;
    }

    private async DurableTask<DurableTaskResponse> SubscribeCoreAsync(DurableTaskAddress address, bool isSubscription)
    {
        var operationWork = new DurableOperationWork(address, isSubscription);
        // The work rides with the subscription's journal write. A replay assigns the same value, so it writes nothing new.
        var value = operationWork.Format();
        if (work.Value != value)
        {
            work.Value = value;
        }

        var subscription = await grainFactory.GetDurableTask<object?>(address)
            .WithId(SubscriptionId)
            .ScheduleAsync();
        return await subscription.GetResponseAsync();
    }
}
