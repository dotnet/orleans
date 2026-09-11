namespace Orleans.Configuration;

/// <summary>
/// Configures how a silo retrieves grain manifests from the cluster.
/// </summary>
public sealed class ClusterManifestOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the silo retrieves manifests by content hash
    /// and uses peer summaries to repair missing manifests.
    /// </summary>
    /// <value>
    /// <see langword="false"/> by default, which retrieves each active silo's manifest directly.
    /// </value>
    /// <remarks>
    /// The runtime captures this setting when the silo's manifest provider is constructed.
    /// Restart the silo to apply a changed value. Silos serve hash requests from enabled peers
    /// on demand, including when their own retrieval uses the default direct path.
    /// Peer repair uses up to three concurrent local attempts, each with a one-second deadline.
    /// </remarks>
    public bool EnableContentAddressedRetrieval { get; set; }
}
