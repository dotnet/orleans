using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Supplies the access log of the durable task attempt whose body the calling code runs as the context of the activation's
/// journaled state, so the body's accesses are recorded and replayed.
/// </summary>
/// <remarks>
/// The provider supplies no context outside a body, once the attempt's log is inactive, and while the log runs an access
/// or the runtime runs one of the attempt's operations. The runtime's own work, even when a body's flow runs it, makes no
/// access which a context routes: its state is created without an access target, and its sends through the outbox run as
/// the journaled state host's work.
/// </remarks>
internal sealed class DurableTaskAccessContextProvider(IDurableTaskGrainRuntime runtime) : JournaledStateContextProvider
{
    public override JournaledStateContext? Current =>
        DurableTaskContext.TryGetCurrent(out var current)
        && current is GrainDurableTaskContext context
        && ReferenceEquals(context.Runtime, runtime)
        && context.GetAccessLogForAccess() is { IsActive: true, Depth: 0 } log
            ? log
            : null;
}
