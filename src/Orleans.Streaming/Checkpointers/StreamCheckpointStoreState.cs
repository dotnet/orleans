using System;

namespace Orleans.Streams;

/// <summary>
/// Represents a persisted stream checkpoint and its backend version.
/// </summary>
public readonly struct StreamCheckpointStoreState : IEquatable<StreamCheckpointStoreState>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamCheckpointStoreState"/> struct.
    /// </summary>
    /// <param name="checkpoint">The checkpoint value.</param>
    /// <param name="version">The backend version or entity tag.</param>
    public StreamCheckpointStoreState(string checkpoint, string version)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(version);
        Checkpoint = checkpoint;
        Version = version;
    }

    /// <summary>
    /// Gets the checkpoint value.
    /// </summary>
    public string Checkpoint { get; }

    /// <summary>
    /// Gets the backend version or entity tag.
    /// </summary>
    public string Version { get; }

    /// <inheritdoc />
    public bool Equals(StreamCheckpointStoreState other)
        => string.Equals(Checkpoint, other.Checkpoint, StringComparison.Ordinal)
            && string.Equals(Version, other.Version, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StreamCheckpointStoreState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Checkpoint, Version);

    /// <summary>Determines whether two persisted checkpoint states are equal.</summary>
    public static bool operator ==(StreamCheckpointStoreState left, StreamCheckpointStoreState right) => left.Equals(right);

    /// <summary>Determines whether two persisted checkpoint states differ.</summary>
    public static bool operator !=(StreamCheckpointStoreState left, StreamCheckpointStoreState right) => !left.Equals(right);
}
