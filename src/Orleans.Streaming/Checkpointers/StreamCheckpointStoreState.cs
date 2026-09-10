using System;

namespace Orleans.Streams;

/// <summary>
/// Represents a persisted stream checkpoint and its backend version.
/// </summary>
public readonly struct StreamCheckpointStoreState
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
}
