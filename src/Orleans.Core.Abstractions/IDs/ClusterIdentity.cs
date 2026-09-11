using System;

namespace Orleans.Runtime;

/// <summary>
/// Identifies an Orleans cluster within a service.
/// </summary>
[Serializable, GenerateSerializer, Immutable]
[Alias("cluster-identity")]
public readonly struct ClusterIdentity : IEquatable<ClusterIdentity>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterIdentity"/> struct.
    /// </summary>
    public ClusterIdentity(string serviceId, string clusterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        ServiceId = serviceId;
        ClusterId = clusterId;
    }

    /// <summary>
    /// Gets the Orleans service identity.
    /// </summary>
    [Id(0)]
    public string ServiceId { get; }

    /// <summary>
    /// Gets the cluster identity.
    /// </summary>
    [Id(1)]
    public string ClusterId { get; }

    /// <inheritdoc/>
    public bool Equals(ClusterIdentity other)
        => string.Equals(ServiceId, other.ServiceId, StringComparison.Ordinal)
            && string.Equals(ClusterId, other.ClusterId, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ClusterIdentity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(ServiceId ?? string.Empty),
            StringComparer.Ordinal.GetHashCode(ClusterId ?? string.Empty));

    /// <inheritdoc/>
    public override string ToString() => $"{ServiceId}/{ClusterId}";

    /// <summary>
    /// Compares two cluster identities for equality.
    /// </summary>
    public static bool operator ==(ClusterIdentity left, ClusterIdentity right) => left.Equals(right);

    /// <summary>
    /// Compares two cluster identities for inequality.
    /// </summary>
    public static bool operator !=(ClusterIdentity left, ClusterIdentity right) => !left.Equals(right);
}
