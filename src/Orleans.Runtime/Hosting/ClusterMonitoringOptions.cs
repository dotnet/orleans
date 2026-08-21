namespace Orleans.Hosting;

/// <summary>
/// Configures how silos reconcile Orleans membership with an external hosting environment.
/// </summary>
public sealed class ClusterMonitoringOptions
{
    /// <summary>
    /// Gets or sets the maximum number of active silos which monitor the external cluster provider.
    /// </summary>
    public int MaxAgents { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of initialization attempts.
    /// </summary>
    public int MaxInitializationAttempts { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether external members corresponding to defunct silos are deleted.
    /// </summary>
    public bool DeleteDefunctClusterMembers { get; set; }
}
