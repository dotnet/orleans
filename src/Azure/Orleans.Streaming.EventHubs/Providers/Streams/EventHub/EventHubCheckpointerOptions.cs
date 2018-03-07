using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    public class EventHubCheckpointerOptions
    {
        /// <summary>
        /// Azure table storage connections string.
        /// </summary>
        [RedactConnectionString]
        public string CheckpointConnectionString { get; set; }
        /// <summary>
        /// Azure table name.
        /// </summary>
        public string CheckpointTableName { get; set; }
        /// <summary>
        /// Interval to write checkpoints.  Prevents spamming storage.
        /// </summary>
        public TimeSpan CheckpointPersistInterval { get; set; } = DEFAULT_CHECKPOINT_PERSIST_INTERVAL;
        public static readonly TimeSpan DEFAULT_CHECKPOINT_PERSIST_INTERVAL = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Unique namespace for checkpoint data.  Is similar to consumer group.
        /// </summary>
        public string CheckpointNamespace { get; set; }
    }
}
