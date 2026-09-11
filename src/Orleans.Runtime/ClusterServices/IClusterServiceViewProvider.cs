namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Supplies canonical, ordered views for one logical service. Providers are registered by service identifier.
/// </summary>
internal interface IClusterServiceViewProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the latest local view, or <see langword="null"/> while awaiting the first authoritative view.
    /// </summary>
    ClusterServiceView? CurrentView { get; }

    /// <summary>
    /// Gets increasing views, beginning with the current view when available. Readers can skip revisions.
    /// </summary>
    IAsyncEnumerable<ClusterServiceView> ViewUpdates { get; }

    /// <summary>
    /// Refreshes the local view to at least <paramref name="minimumView"/>.
    /// A null minimum requests a refresh even when a view is already available.
    /// A provider must reject a minimum from an authority epoch it cannot serve.
    /// </summary>
    ValueTask<ClusterServiceView> RefreshViewAsync(
        ClusterServiceViewId? minimumView,
        CancellationToken cancellationToken);
}
