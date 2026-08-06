using System;
using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures grain-based stream queue checkpointing.
    /// </summary>
    public sealed class GrainStreamQueueCheckpointerOptions
    {
        /// <summary>
        /// Gets or sets the minimum interval between checkpoint writes.
        /// </summary>
        public TimeSpan PersistInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the name of the grain storage provider used to persist checkpoints.
        /// </summary>
        public string StorageProviderName { get; set; } = ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME;

        /// <summary>
        /// Gets or sets the comparer used to prevent a checkpoint from moving backwards.
        /// </summary>
        /// <remarks>
        /// The candidate checkpoint is compared with the latest checkpoint and accepted only when it compares greater.
        /// When this property is <see langword="null"/>, checkpoints are assumed to arrive in increasing order.
        /// Use <see cref="StreamCheckpointComparers.Numeric"/> for numeric checkpoint values of arbitrary size.
        /// </remarks>
        public IComparer<string>? CheckpointComparer { get; set; }
    }
}
