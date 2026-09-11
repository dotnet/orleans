using System;
using System.Collections.Generic;

namespace Orleans.Configuration;

/// <summary>
/// Configures federation between Orleans clusters which share a service identity.
/// </summary>
public sealed class MetaclusterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether metacluster reference semantics are enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets statically configured relay endpoints by cluster identity.
    /// </summary>
    public Dictionary<string, Uri[]> Clusters { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets system-target grain types which can receive requests from other clusters.
    /// </summary>
    public HashSet<string> ExportedSystemTargets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the duration of directory-backed cluster ownership leases.
    /// </summary>
    public TimeSpan ClusterOwnershipLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how close to expiration an ownership lease can be before it is renewed.
    /// </summary>
    public TimeSpan ClusterOwnershipLeaseRenewalWindow { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets the duration for cached virtual-reference locations.
    /// </summary>
    public TimeSpan ClusterLocationCacheDuration { get; set; } = TimeSpan.FromMinutes(1);
}
