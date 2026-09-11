using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement;

/// <summary>
/// In-memory cluster directory intended for development and testing.
/// </summary>
public sealed class InMemoryClusterDirectory : IClusterDirectory
{
    private readonly ConcurrentDictionary<GrainId, ClusterDirectoryEntry> _entries = new();
    private readonly TimeProvider _timeProvider;
    private long _nextVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryClusterDirectory"/> class.
    /// </summary>
    public InMemoryClusterDirectory()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryClusterDirectory"/> class.
    /// </summary>
    public InMemoryClusterDirectory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public ValueTask<ClusterDirectoryEntry?> Lookup(
        GrainId grainId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        return new ValueTask<ClusterDirectoryEntry?>(
            _entries.TryGetValue(grainId, out var entry) && entry.LeaseExpiration > now ? entry : null);
    }

    /// <inheritdoc/>
    public ValueTask<ClusterDirectoryEntry> GetOrCreate(
        GrainId grainId,
        string proposedClusterId,
        long topologyEpoch,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLeaseArguments(grainId, leaseDuration);
        var now = _timeProvider.GetUtcNow();
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedClusterId);
        while (true)
        {
            if (!_entries.TryGetValue(grainId, out var current))
            {
                var created = CreateEntry(grainId);
                if (_entries.TryAdd(grainId, created))
                {
                    return new ValueTask<ClusterDirectoryEntry>(created);
                }

                continue;
            }

            if (current.LeaseExpiration > now)
            {
                return new ValueTask<ClusterDirectoryEntry>(current);
            }

            if (topologyEpoch < current.TopologyEpoch)
            {
                throw new InvalidOperationException(
                    $"Topology epoch '{topologyEpoch}' cannot replace ownership from newer epoch '{current.TopologyEpoch}'.");
            }

            var replacement = CreateEntry(grainId);
            if (_entries.TryUpdate(grainId, replacement, current))
            {
                return new ValueTask<ClusterDirectoryEntry>(replacement);
            }
        }

        ClusterDirectoryEntry CreateEntry(GrainId id)
        {
            var version = Interlocked.Increment(ref _nextVersion);
            return new ClusterDirectoryEntry(
                id,
                proposedClusterId,
                version,
                topologyEpoch,
                version,
                now + leaseDuration);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ClusterDirectoryEntry?> TryRenew(
        GrainId grainId,
        long expectedVersion,
        string ownerClusterId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLeaseArguments(grainId, leaseDuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerClusterId);
        var now = _timeProvider.GetUtcNow();
        while (_entries.TryGetValue(grainId, out var current))
        {
            if (current.Version != expectedVersion
                || !string.Equals(current.ClusterId, ownerClusterId, StringComparison.Ordinal)
                || current.LeaseExpiration <= now)
            {
                return new ValueTask<ClusterDirectoryEntry?>((ClusterDirectoryEntry?)null);
            }

            var replacement = new ClusterDirectoryEntry(
                current.GrainId,
                current.ClusterId,
                current.Version,
                current.TopologyEpoch,
                current.FencingToken,
                now + leaseDuration);
            if (_entries.TryUpdate(grainId, replacement, current))
            {
                return new ValueTask<ClusterDirectoryEntry?>(replacement);
            }
        }

        return new ValueTask<ClusterDirectoryEntry?>((ClusterDirectoryEntry?)null);
    }

    /// <inheritdoc/>
    public ValueTask<ClusterDirectoryEntry?> TryMove(
        GrainId grainId,
        long expectedVersion,
        string destinationClusterId,
        long topologyEpoch,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLeaseArguments(grainId, leaseDuration);
        var now = _timeProvider.GetUtcNow();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationClusterId);
        while (_entries.TryGetValue(grainId, out var current))
        {
            if (current.Version != expectedVersion)
            {
                return new ValueTask<ClusterDirectoryEntry?>((ClusterDirectoryEntry?)null);
            }

            if (current.LeaseExpiration > now)
            {
                return new ValueTask<ClusterDirectoryEntry?>((ClusterDirectoryEntry?)null);
            }

            if (topologyEpoch < current.TopologyEpoch)
            {
                return new ValueTask<ClusterDirectoryEntry?>((ClusterDirectoryEntry?)null);
            }

            var version = Interlocked.Increment(ref _nextVersion);
            var replacement = new ClusterDirectoryEntry(
                grainId,
                destinationClusterId,
                version,
                topologyEpoch,
                version,
                now + leaseDuration);
            if (_entries.TryUpdate(grainId, replacement, current))
            {
                return new ValueTask<ClusterDirectoryEntry?>(replacement);
            }
        }

        return new ValueTask<ClusterDirectoryEntry?>((ClusterDirectoryEntry?)null);
    }

    private static void ValidateLeaseArguments(GrainId grainId, TimeSpan leaseDuration)
    {
        if (grainId.IsDefault)
        {
            throw new ArgumentException("The grain identity must be initialized.", nameof(grainId));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }
}
