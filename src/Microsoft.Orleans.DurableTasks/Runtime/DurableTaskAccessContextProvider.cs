using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Supplies the access log of the durable task attempt whose body the calling code runs as the context of the activation's
/// journaled state, so the body's accesses are recorded and replayed.
/// </summary>
/// <remarks>
/// The provider supplies no context outside a body, after the runtime closed the attempt's log, while the log runs an
/// access or the runtime runs one of the attempt's operations, and within the runtime's own work which a body's flow
/// started, which <see cref="DurableTaskHostFlow"/> marks.
/// </remarks>
internal sealed class DurableTaskAccessContextProvider(IDurableTaskGrainRuntime runtime) : JournaledStateContextProvider
{
    public override JournaledStateContext? Current =>
        DurableTaskContext.TryGetCurrent(out var current)
        && current is GrainDurableTaskContext { AccessLog: { IsOpen: true, Depth: 0 } log } context
        && ReferenceEquals(context.Runtime, runtime)
        && !DurableTaskHostFlow.IsActive
            ? log
            : null;
}

/// <summary>
/// Marks the runtime's work which a durable task body's flow starts and which continues across awaits, such as scheduling
/// a child or recording a child's response, so the accesses to journaled state which it makes aren't the body's.
/// </summary>
/// <remarks>
/// The mark flows with the work's asynchronous flow and ends where the runtime enters a body, so a child's body records its
/// own accesses. The runtime's synchronous work on behalf of an attempt uses
/// <see cref="DurableTaskAccessLog.EnterOperation"/> instead, which flows nothing.
/// </remarks>
internal static class DurableTaskHostFlow
{
    private static readonly AsyncLocal<object?> Current = new();
    private static readonly object Marker = new();

    /// <summary>Gets whether the calling code is the runtime's work.</summary>
    public static bool IsActive => Current.Value is not null;

    /// <summary>Marks the calling flow as the runtime's work until the returned value is disposed.</summary>
    public static Restorer Enter() => Set(Marker);

    /// <summary>Ends the mark for the calling flow, which runs a body, until the returned value is disposed.</summary>
    public static Restorer Exit() => Set(null);

    private static Restorer Set(object? value)
    {
        var previous = Current.Value;
        if (ReferenceEquals(previous, value))
        {
            return default;
        }

        Current.Value = value;
        return new(previous, changed: true);
    }

    /// <summary>Restores the mark which applied before <see cref="Enter"/> or <see cref="Exit"/>.</summary>
    public readonly struct Restorer(object? previous, bool changed) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose()
        {
            if (changed)
            {
                Current.Value = previous;
            }
        }
    }
}
