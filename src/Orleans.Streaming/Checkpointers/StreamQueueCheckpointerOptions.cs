using System;
using System.Collections.Generic;

namespace Orleans.Streams;

/// <summary>
/// Configures a <see cref="StreamQueueCheckpointer"/>.
/// </summary>
public sealed class StreamQueueCheckpointerOptions
{
    /// <summary>
    /// Gets or sets the minimum interval between checkpoint writes.
    /// </summary>
    public TimeSpan PersistInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the comparer used to prevent a checkpoint from moving backwards.
    /// </summary>
    /// <remarks>
    /// When this property is <see langword="null"/>, checkpoints are assumed to arrive in increasing order.
    /// </remarks>
    public IComparer<string>? CheckpointComparer { get; set; }
}
