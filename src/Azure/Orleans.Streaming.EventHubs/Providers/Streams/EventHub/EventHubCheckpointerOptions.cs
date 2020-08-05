using Orleans.Streaming.EventHubs;
using System;

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
    }

    //TOOD: how to wire this validator into DI?
    public class AzureTableStreamCheckpointerOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableStreamCheckpointerOptions>
    {
        public AzureTableStreamCheckpointerOptionsValidator(AzureTableStreamCheckpointerOptions options, string name) : base(options, name)
        {
        }
    }
}
