using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Starts the executions which a durable grain's activation recovered once the activation has completed, and aborts
/// them when the activation starts deactivating.
/// </summary>
/// <remarks>
/// <para>
/// Grain lifecycle stages start before the grain's <c>OnActivateAsync</c> runs and stop after its
/// <c>OnDeactivateAsync</c>. The runtime therefore recovers task state during its lifecycle stage and defers running
/// grain code until the activation is valid, which is when the activation joins the working set.
/// </para>
/// <para>
/// When deactivation begins, the runtime stops accepting work and signals its running executions to abort before the
/// grain's <c>OnDeactivateAsync</c> runs. The runtime's lifecycle stage then waits for the aborted executions, before
/// messaging and the journal stop.
/// </para>
/// <para>
/// Both callbacks queue their work on the activation's scheduler. The observer applies to the grain classes which
/// <see cref="DurableTaskGrainTypeConfigurator"/> binds to the runtime.
/// </para>
/// </remarks>
internal sealed class DurableTaskActivationObserver : IActivationWorkingSetObserver
{
    public void OnAdded(IActivationWorkingSetMember member)
    {
        if (member is IGrainContext { GrainInstance: DurableGrain or IMessagingGrain } context)
        {
            context.Scheduler.QueueAction(() => GetRuntime(context).StartRecoveredExecutions());
        }
    }

    public void OnDeactivating(IActivationWorkingSetMember member)
    {
        if (member is IGrainContext { GrainInstance: DurableGrain or IMessagingGrain } context)
        {
            // The runtime's lifecycle stage awaits the same stop.
            context.Scheduler.QueueAction(() => _ = GetRuntime(context).StopAsync(CancellationToken.None));
        }
    }

    private static DurableTaskGrainRuntime GetRuntime(IGrainContext context) =>
        context.ActivationServices.GetRequiredService<DurableTaskGrainRuntime>();
}
