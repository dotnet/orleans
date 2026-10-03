using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Identifies one immutable definition published by a service's authority.
/// </summary>
internal interface IClusterServiceView<TViewId>
{
    TViewId Id { get; }

    /// <summary>
    /// Gets the authoritative predecessor, including when a reader skips updates.
    /// </summary>
    bool TryGetPredecessor(out TViewId predecessor);
}

/// <summary>
/// Supplies canonical, ordered views for one logical service. Providers are registered by service identifier.
/// </summary>
internal interface IClusterServiceViewProvider<TViewId, TView> : IAsyncDisposable
    where TView : IClusterServiceView<TViewId>
{
    /// <summary>
    /// Gets the latest local view when an authoritative definition is available.
    /// </summary>
    bool TryGetCurrentView([MaybeNullWhen(false)] out TView view);

    /// <summary>
    /// Gets increasing views, beginning with the current view when available. Readers can skip revisions.
    /// </summary>
    IAsyncEnumerable<TView> ViewUpdates { get; }

    /// <summary>
    /// Refreshes the authoritative definition, propagating unavailability, cancellation, and failure.
    /// </summary>
    ValueTask<TView> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns a definition at or after the requested position within the configured authority.
    /// Providers reject requests for other authority namespaces.
    /// </summary>
    ValueTask<TView> RefreshAtLeastAsync(
        TViewId minimumView,
        CancellationToken cancellationToken);
}
