using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Hosts one HTTP operation: a root task which subscribes to the task that performs the operation's work, and completes
/// with that task's response.
/// </summary>
/// <remarks>
/// <para>
/// The grain's key is the operation's key, which derives from its scope and identifier, so every request which names the
/// operation reaches this grain without knowing which grain runs the operation's work. The root task's result is the
/// work's terminal response, including a failure or cancellation, so the operation reports it exactly.
/// </para>
/// <para>
/// The work is a root task on its grain: a durable grain call, which the host starts as a detached root named by the
/// operation's key, or an existing task, which a reference names. Requests which signal or cancel the operation address
/// that root on its grain directly.
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

    /// <summary>Subscribes to the existing task <paramref name="taskId"/> on <paramref name="target"/>, and returns its terminal response.</summary>
    [Alias("Subscribe")]
    DurableTask<DurableTaskResponse> SubscribeAsync(GrainId target, TaskId taskId);

    /// <summary>
    /// Returns the task which performs the operation's work once its grain holds it, or <see langword="null"/> when the
    /// operation ended without one, such as when its grain rejected the call.
    /// </summary>
    /// <param name="isCompleted">
    /// Whether the caller observed that the operation completed. A completed operation recorded its target, if it had one,
    /// before its completion, so the host answers at once rather than waiting for a target.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait for a running operation's target.</param>
    [Alias("GetTarget"), Orleans.Concurrency.AlwaysInterleave]
    Task<DurableOperationTarget?> GetTargetAsync(bool isCompleted, CancellationToken cancellationToken = default);
}

/// <summary>Identifies the task which performs an operation's work.</summary>
/// <param name="Grain">The grain which holds the task.</param>
/// <param name="TaskId">The task's identifier on <paramref name="Grain"/>.</param>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableOperationTarget")]
internal sealed record DurableOperationTarget(
    [property: Id(0)] GrainId Grain,
    [property: Id(1)] TaskId TaskId)
{
    /// <summary>Formats the target as <c>{grain length}:{grain}{task}</c>, which <see cref="Parse"/> reads.</summary>
    public string Format()
    {
        var grain = Grain.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{grain.Length}:{grain}{TaskId}");
    }

    public static DurableOperationTarget Parse(string value)
    {
        var separator = value.IndexOf(':');
        var grainLength = int.Parse(value.AsSpan(0, separator), CultureInfo.InvariantCulture);
        return new(
            GrainId.Parse(value.Substring(separator + 1, grainLength)),
            TaskId.Parse(value[(separator + 1 + grainLength)..]));
    }
}

[GrainType("orleans-durable-operation")]
internal sealed class DurableOperationGrain(
    [FromKeyedServices("target")] IDurableValue<string> target,
    IGrainFactory grainFactory) : DurableGrain, IDurableOperationGrain
{
    /// <summary>The root identifier of the operation's task on its host.</summary>
    public const string RootId = "operation";

    /// <summary>The identifier segment of the host's subscription to the operation's work.</summary>
    private const string WorkId = "work";


    private readonly TaskCompletionSource<DurableOperationTarget?> _target = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async DurableTask<DurableTaskResponse> RunAsync(DurableTask call)
    {
        try
        {
            // The call runs as a root on its grain, named by the operation's key, which is unique among the operations
            // that call the grain. Scheduling returns once the grain holds it, so requests which then signal or cancel the
            // operation find it.
            var root = await call.Detached().WithId(this.GetPrimaryKeyString()).ScheduleAsync();
            return await SubscribeCoreAsync(DurableOperationService.GetTarget(call), root.TargetTaskId);
        }
        finally
        {
            _target.TrySetResult(null);
        }
    }

    public async DurableTask<DurableTaskResponse> SubscribeAsync(GrainId grain, TaskId taskId)
    {
        try
        {
            return await SubscribeCoreAsync(grain, taskId);
        }
        finally
        {
            _target.TrySetResult(null);
        }
    }

    public Task<DurableOperationTarget?> GetTargetAsync(bool isCompleted, CancellationToken cancellationToken) =>
        target.Value is { } value ? Task.FromResult<DurableOperationTarget?>(DurableOperationTarget.Parse(value))
        : isCompleted ? Task.FromResult<DurableOperationTarget?>(null)
        : _target.Task.WaitAsync(cancellationToken);

    private async DurableTask<DurableTaskResponse> SubscribeCoreAsync(GrainId grain, TaskId taskId)
    {
        Record(new(grain, taskId));
        var subscription = await grainFactory.GetGrain(grain)
            .GetDurableTask<object?>(taskId)
            .WithId(WorkId)
            .ScheduleAsync();
        return await subscription.GetResponseAsync();
    }

    /// <summary>Records the operation's target with the execution's next journal write, and publishes it.</summary>
    private void Record(DurableOperationTarget operationTarget)
    {
        target.Value = operationTarget.Format();
        _target.TrySetResult(operationTarget);
    }
}
