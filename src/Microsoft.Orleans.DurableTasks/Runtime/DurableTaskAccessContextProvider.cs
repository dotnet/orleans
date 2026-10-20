using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Supplies the access log of the durable task attempt whose body the calling code runs as the context of the activation's
/// journaled state, so the body's accesses are recorded and replayed.
/// </summary>
/// <remarks>
/// The provider supplies no context outside a body, once the attempt's log is inactive, and within a covered region of the
/// attempt, such as an access which the log runs, an effect, or the runtime's operation on behalf of the attempt, whose
/// record covers the region's accesses. The runtime's own work, even when a body's flow runs it, makes no
/// access which a context routes: its state is created without an access target, and its sends through the outbox run as
/// the journaled state host's work.
/// </remarks>
internal sealed class DurableTaskAccessContextProvider(IDurableTaskGrainRuntime runtime) : JournaledStateContextProvider
{
    public override JournaledStateContext? Current =>
        DurableTaskContext.TryGetCurrent(out var current)
        && current is GrainDurableTaskContext { IsInCoveredRegion: false } context
        && ReferenceEquals(context.Runtime, runtime)
        && context.GetAccessLogForAccess() is { IsActive: true } log
            ? log
            : null;
}
