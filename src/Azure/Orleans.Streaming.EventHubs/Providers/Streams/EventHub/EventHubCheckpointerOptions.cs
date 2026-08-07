using System;
using System.Collections.Generic;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;

namespace Orleans.Configuration
{
    public class AzureTableStreamCheckpointerOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Azure table name.
        /// </summary>
        public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "Checkpoint";

        /// <summary>
        /// Interval to write checkpoints.  Prevents spamming storage.
        /// </summary>
        public TimeSpan PersistInterval { get; set; } = DEFAULT_CHECKPOINT_PERSIST_INTERVAL;
        public static readonly TimeSpan DEFAULT_CHECKPOINT_PERSIST_INTERVAL = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the comparer used to prevent a checkpoint from moving backwards.
        /// </summary>
        /// <remarks>
        /// When this property is <see langword="null"/>, checkpoints are assumed to arrive in increasing order.
        /// Use <see cref="StreamCheckpointComparers.Numeric"/> for numeric checkpoint values of arbitrary size.
        /// </remarks>
        public IComparer<string>? CheckpointComparer { get; set; }
    }

    public class AzureTableStreamCheckpointerOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableStreamCheckpointerOptions>
    {
        public AzureTableStreamCheckpointerOptionsValidator(AzureTableStreamCheckpointerOptions options, string name) : base(options, name)
        {
        }
    }
}
