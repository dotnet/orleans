using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Stores mutable grain-to-cluster ownership.
/// </summary>
public interface IClusterDirectory
{
    /// <summary>
    /// Looks up the current unexpired owner of the provided grain.
    /// </summary>
    ValueTask<ClusterDirectoryEntry?> Lookup(
        GrainId grainId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current owner or atomically creates an ownership record.
    /// </summary>
    ValueTask<ClusterDirectoryEntry> GetOrCreate(
        GrainId grainId,
        string proposedClusterId,
        long topologyEpoch,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an ownership lease when the expected version and owner are current.
    /// </summary>
    ValueTask<ClusterDirectoryEntry?> TryRenew(
        GrainId grainId,
        long expectedVersion,
        string ownerClusterId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically moves ownership when the expected version is current and its lease has expired.
    /// </summary>
    ValueTask<ClusterDirectoryEntry?> TryMove(
        GrainId grainId,
        long expectedVersion,
        string destinationClusterId,
        long topologyEpoch,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes mutable grain ownership in a metacluster.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record ClusterDirectoryEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterDirectoryEntry"/> class.
    /// </summary>
    public ClusterDirectoryEntry(
        GrainId grainId,
        string clusterId,
        long version,
        long topologyEpoch,
        long fencingToken,
        DateTimeOffset leaseExpiration)
    {
        if (grainId.IsDefault)
        {
            throw new ArgumentException("The grain identity must be initialized.", nameof(grainId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (fencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        }

        if (leaseExpiration == default)
        {
            throw new ArgumentException("The lease expiration must be initialized.", nameof(leaseExpiration));
        }

        GrainId = grainId;
        ClusterId = clusterId;
        Version = version;
        TopologyEpoch = topologyEpoch;
        FencingToken = fencingToken;
        LeaseExpiration = leaseExpiration;
    }

    /// <summary>
    /// Gets the grain identity.
    /// </summary>
    [Id(0)]
    public GrainId GrainId { get; }

    /// <summary>
    /// Gets the owner cluster.
    /// </summary>
    [Id(1)]
    public string ClusterId { get; }

    /// <summary>
    /// Gets the ownership version.
    /// </summary>
    [Id(2)]
    public long Version { get; }

    /// <summary>
    /// Gets the topology epoch used for this decision.
    /// </summary>
    [Id(3)]
    public long TopologyEpoch { get; }

    /// <summary>
    /// Gets the monotonic fencing token.
    /// </summary>
    [Id(4)]
    public long FencingToken { get; }

    /// <summary>
    /// Gets the ownership lease expiration.
    /// </summary>
    [Id(5)]
    public DateTimeOffset LeaseExpiration { get; }
}

/// <summary>
/// Validates directory-backed ownership before a request is dispatched.
/// </summary>
public interface IClusterOwnershipValidator
{
    /// <summary>
    /// Validates and renews local ownership for the provided grain.
    /// </summary>
    ValueTask<ClusterDirectoryEntry> ValidateLocalOwnership(
        GrainId grainId,
        string localClusterId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides the cluster ownership record associated with the current grain invocation.
/// </summary>
public interface IClusterOwnershipAccessor
{
    /// <summary>
    /// Gets the current ownership record, if the grain uses directory-backed cluster location.
    /// </summary>
    ClusterDirectoryEntry? Current { get; }
}
