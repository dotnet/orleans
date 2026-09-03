using System;
using System.Collections.Generic;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures Azure Table Storage checkpoint persistence for an Event Hubs stream provider.
    /// </summary>
    public class AzureTableStreamCheckpointerOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Azure table name.
        /// </summary>
        public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
        /// <summary>
        /// The default Azure Table Storage table name.
        /// </summary>
        public const string DEFAULT_TABLE_NAME = "Checkpoint";

        /// <summary>
        /// Interval to write checkpoints.  Prevents spamming storage.
        /// </summary>
        public TimeSpan PersistInterval { get; set; } = DEFAULT_CHECKPOINT_PERSIST_INTERVAL;
        /// <summary>
        /// The default minimum interval between checkpoint writes.
        /// </summary>
        public static readonly TimeSpan DEFAULT_CHECKPOINT_PERSIST_INTERVAL = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the prefix applied to checkpoint partition keys.
        /// </summary>
        public string PartitionKeyPrefix { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the comparer used to prevent a checkpoint from moving backwards.
        /// </summary>
        /// <remarks>
        /// When this property is <see langword="null"/>, checkpoints are assumed to arrive in increasing order.
        /// Use <see cref="StreamCheckpointComparers.Numeric"/> for numeric checkpoint values of arbitrary size.
        /// </remarks>
        public IComparer<string>? CheckpointComparer { get; set; }
    }

    /// <summary>
    /// Validates <see cref="AzureTableStreamCheckpointerOptions"/>.
    /// </summary>
    public class AzureTableStreamCheckpointerOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableStreamCheckpointerOptions>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AzureTableStreamCheckpointerOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        /// <param name="name">The options name.</param>
        public AzureTableStreamCheckpointerOptionsValidator(AzureTableStreamCheckpointerOptions options, string name) : base(options, name)
        {
        }
    }
}
