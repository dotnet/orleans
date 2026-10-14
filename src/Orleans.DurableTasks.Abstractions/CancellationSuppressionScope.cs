using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

/// <summary>
/// A scope, started by <see cref="DurableTaskContext.SuppressCancellation"/>, in which a durable execution schedules
/// children which its durable cancellation doesn't reach.
/// </summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "A scope is a disposal token whose equality is not part of its contract.")]
public readonly struct CancellationSuppressionScope : IDisposable
{
    private readonly DurableTaskContext? _previous;

    internal CancellationSuppressionScope(DurableTaskContext? previous) => _previous = previous;

    /// <summary>Ends the scope, restoring the suppression state which preceded it.</summary>
    public void Dispose() => DurableTaskContext.RestoreCancellationSuppression(_previous);
}
