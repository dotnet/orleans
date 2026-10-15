using System;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

/// <summary>
/// The address of a durable task which other parties reach: the grain which stores the task and the task's identifier on
/// that grain.
/// </summary>
/// <param name="Target">The grain which stores the task.</param>
/// <param name="Id">The task's identifier on <paramref name="Target"/>.</param>
/// <remarks>
/// <para>
/// An address is a value which code stores and sends: Orleans serializes it in grain calls and journaled state, and
/// System.Text.Json serializes it as <c>{"Target":"{grain type}/{grain key}","Id":"{task ID}"}</c>, so JSON journals and
/// HTTP payloads carry it without registration.
/// <see cref="DurableTaskAddressExtensions.GetDurableTask{TResult}(IGrainFactory, DurableTaskAddress)"/> turns it back into
/// a reference, which awaits, polls, cancels, or aborts the task.
/// </para>
/// <para>
/// An address names a task which its grain retains for other parties: a root task, which a client, a grain, or a durable
/// execution started, including a detached task, or a task which a client or another grain invoked. A grain retains a
/// terminal root task for <c>DurableTaskOptions.ResultRetentionPeriod</c>, after which a lookup throws
/// <see cref="DurableTaskNotFoundException"/>.
/// </para>
/// </remarks>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.DurableTaskAddress")]
public readonly record struct DurableTaskAddress(
    [property: Id(0)] GrainId Target,
    [property: Id(1)] TaskId Id) : IDurableTaskReference;

/// <summary>Creates and resolves the addresses of durable tasks.</summary>
public static class DurableTaskAddressExtensions
{
    /// <summary>Gets a reference to the durable task at <paramref name="address"/>, without scheduling it.</summary>
    /// <typeparam name="TResult">The durable task result type.</typeparam>
    /// <param name="grainFactory">The grain factory, such as a grain's <c>GrainFactory</c> or an <see cref="IClusterClient"/>.</param>
    /// <param name="address">The task's address.</param>
    /// <returns>
    /// A reference which behaves as
    /// <see cref="DurableTaskGrainExtensions.GetDurableTask{TResult}(IAddressable, TaskId)"/> describes.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="grainFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="address"/> has no grain or no task identifier.</exception>
    public static DurableTaskReference<TResult> GetDurableTask<TResult>(this IGrainFactory grainFactory, DurableTaskAddress address)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        if (address.Target.IsDefault || address.Id.IsDefault)
        {
            throw new ArgumentException("A durable task address requires a grain and a task identifier.", nameof(address));
        }

        return grainFactory.GetGrain(address.Target).GetDurableTask<TResult>(address.Id);
    }

    /// <summary>Gets the address of the task which <paramref name="task"/> observes, when other parties reach it.</summary>
    /// <param name="task">The handle.</param>
    /// <param name="address">The address of the task, when this method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the handle's task is one which its grain retains for other parties: a root task which
    /// a client or an ordinary grain method started, or, in a durable execution, a detached task or a task which
    /// <c>GetDurableTask</c> observes. An attached child, which the execution's tree owns, has no address.
    /// </returns>
    /// <remarks>The address's identifier is the handle's <see cref="ScheduledTask.TargetTaskId"/>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    public static bool TryGetAddress(this ScheduledTask task, out DurableTaskAddress address)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.GetFeature<IDurableTaskReference>() is { } reference)
        {
            address = new(reference.Target, reference.Id);
            return true;
        }

        address = default;
        return false;
    }
}
