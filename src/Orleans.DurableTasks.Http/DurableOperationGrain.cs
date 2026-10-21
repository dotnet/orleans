using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

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
    /// reaches it. An operation whose host failed to start its work records it as <see cref="DurableOperationWorkKind.NotStarted"/>.
    /// </remarks>
    [Alias("WaitForWork"), Orleans.Concurrency.AlwaysInterleave]
    Task<DurableOperationWork?> WaitForWorkAsync(TimeSpan timeout);
}

/// <summary>Identifies the kind of an operation's work.</summary>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.Http.DurableOperationWorkKind")]
internal enum DurableOperationWorkKind
{
    /// <summary>A durable grain call, which the operation runs.</summary>
    Run,

    /// <summary>
    /// A durable grain call which the operation's host failed to start on its grain: the call never ran, or whether it
    /// ran is unknown, as the operation's error reports.
    /// </summary>
    NotStarted,

    /// <summary>An existing task, which the operation subscribes to and which other parties own.</summary>
    Subscription,
}

/// <summary>Identifies an operation's work: what the operation performs, and the task which performs it.</summary>
/// <param name="Kind">The kind of work.</param>
/// <param name="Target">The grain which holds the work's task.</param>
/// <param name="Method">
/// For a call, the identity of the interface method which it invokes, the formatted type of its invokable; otherwise empty.
/// </param>
/// <param name="TaskId">
/// The work's task on <paramref name="Target"/>, or <see langword="null"/> for a call which a request describes before its
/// task exists, and for work which never started.
/// </param>
/// <remarks>
/// Work is the same as other work when it calls the same method on the same grain, or subscribes to the same task, as
/// the durable task runtime identifies operations: by what they perform, not by how their arguments serialize.
/// </remarks>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableOperationWork")]
internal sealed record DurableOperationWork(
    [property: Id(0)] DurableOperationWorkKind Kind,
    [property: Id(1)] GrainId Target,
    [property: Id(2)] string Method,
    [property: Id(3)] TaskId? TaskId)
{
    /// <summary>Gets the address of the work's task, or <see langword="null"/> for work which never started.</summary>
    public DurableTaskAddress? Address => TaskId is { } taskId ? new(Target, taskId) : null;

    /// <summary>Gets whether the operation subscribes to an existing task, which other parties own.</summary>
    public bool IsSubscription => Kind == DurableOperationWorkKind.Subscription;

    /// <summary>Returns the work which <paramref name="work"/>, a call or a reference to an existing task, describes.</summary>
    public static DurableOperationWork Describe(DurableTask work) => work switch
    {
        IDurableTaskReference reference => new(DurableOperationWorkKind.Subscription, reference.Target, string.Empty, reference.Id),
        _ when work.TryGetTarget(out var target) => new(DurableOperationWorkKind.Run, target, RuntimeTypeNameFormatter.Format(work.GetType()), TaskId: null),
        _ => throw new InvalidOperationException(
            $"A durable operation runs a durable grain call, such as 'grain.MethodAsync(...)', or subscribes to an existing task, which 'grain.GetDurableTask<T>(id)' references, not '{work.GetType()}'."),
    };

    /// <summary>Returns whether this work performs what <paramref name="other"/> performs.</summary>
    public bool IsSameWorkAs(DurableOperationWork other) => IsSubscription
        ? other.IsSubscription && Target == other.Target && TaskId == other.TaskId
        : !other.IsSubscription && Target == other.Target && Method == other.Method;

    /// <summary>
    /// Formats the work as <c>{kind}{grain length}:{grain}{method length}:{method}{task}</c>, where the kind is <c>r</c>,
    /// <c>n</c>, or <c>s</c>, which <see cref="Parse"/> reads.
    /// </summary>
    /// <remarks>The journal stores the text, which every journal format serializes without application configuration.</remarks>
    public string Format()
    {
        var kind = Kind switch
        {
            DurableOperationWorkKind.Run => 'r',
            DurableOperationWorkKind.NotStarted => 'n',
            _ => 's',
        };
        var grain = Target.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{kind}{grain.Length}:{grain}{Method.Length}:{Method}{TaskId?.ToString()}");
    }

    public static DurableOperationWork Parse(string value)
    {
        var kind = value[0] switch
        {
            'r' => DurableOperationWorkKind.Run,
            'n' => DurableOperationWorkKind.NotStarted,
            _ => DurableOperationWorkKind.Subscription,
        };
        var position = 1;
        var grain = ReadField(value, ref position);
        var method = ReadField(value, ref position);
        return new(kind, GrainId.Parse(grain), method, position == value.Length ? null : Orleans.DurableTasks.TaskId.Parse(value[position..]));

        static string ReadField(string value, ref int position)
        {
            var separator = value.IndexOf(':', position);
            var length = int.Parse(value.AsSpan(position, separator - position), CultureInfo.InvariantCulture);
            position = separator + 1 + length;
            return value.Substring(separator + 1, length);
        }
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
        var described = DurableOperationWork.Describe(call);
        ScheduledTask root;
        try
        {
            root = await call.Detached(this.GetPrimaryKeyString()).ScheduleAsync();
        }
        catch (Exception exception) when (DurableOperationStartException.FromScheduleFailure(exception) is { } failure)
        {
            // The journal records that the work wasn't started with the operation's failure, so requests which wait for
            // the work end.
            work.Value = (described with { Kind = DurableOperationWorkKind.NotStarted }).Format();
            throw failure;
        }

        var recorded = root.TryGetAddress(out var address);
        Debug.Assert(recorded && address.Target == described.Target, "A detached root has an address on the call's grain.");
        return await SubscribeCoreAsync(described with { TaskId = address.Id });
    }

    public async DurableTask<DurableTaskResponse> SubscribeAsync(DurableTaskAddress address) =>
        await SubscribeCoreAsync(new(DurableOperationWorkKind.Subscription, address.Target, string.Empty, address.Id));

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

    private async DurableTask<DurableTaskResponse> SubscribeCoreAsync(DurableOperationWork operationWork)
    {
        // The work rides with the subscription's journal write. A replay assigns the same value, so it writes nothing new.
        var value = operationWork.Format();
        if (work.Value != value)
        {
            work.Value = value;
        }

        var subscription = await grainFactory.GetDurableTask<object?>(operationWork.Address!.Value)
            .WithId(SubscriptionId)
            .ScheduleAsync();
        return await subscription.GetResponseAsync();
    }
}
