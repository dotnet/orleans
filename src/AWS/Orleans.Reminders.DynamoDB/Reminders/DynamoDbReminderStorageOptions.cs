using Orleans.Reminders.DynamoDB;

namespace Orleans.Configuration
{
    /// <summary>
    /// Controls the DynamoDB reminder schema migration protocol.
    /// </summary>
    public enum DynamoDBReminderTableMode
    {
        /// <summary>
        /// Uses the legacy table exclusively. This mode is compatible with older Orleans binaries.
        /// </summary>
        Legacy,

        /// <summary>
        /// Creates and backfills the V2 table, then writes both schemas atomically while continuing to read V1.
        /// </summary>
        Migrate,

        /// <summary>
        /// Completes and verifies migration, requires all active silos to be V2-capable, and reads V2.
        /// Writes continue to both schemas to retain the rollback window.
        /// </summary>
        V2,

        /// <summary>
        /// Verifies both schemas and returns reads to V1. Writes continue to both schemas.
        /// </summary>
        Rollback,

        /// <summary>
        /// Irreversibly retires V1 after verifying it for the last time, then reads and writes only V2.
        /// </summary>
        V2Only,
    }

    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderStorageOptions : DynamoDBClientOptions
    {
        /// <summary>
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

        /// <summary>
        /// Use Provisioned Throughput for tables
        /// </summary>
        public bool UseProvisionedThroughput { get; set; } = true;

        /// <summary>
        /// Create the table if it doesn't exist
        /// </summary>
        public bool CreateIfNotExists { get; set; } = true;

        /// <summary>
        /// Update the table if it exists
        /// </summary>
        public bool UpdateIfExists { get; set; } = true;

        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansReminders'.
        /// </summary>
        public string TableName { get; set; } = "OrleansReminders";

        /// <summary>
        /// Gets or sets the V2 table name. When unset, <c>-v2</c> is appended to <see cref="TableName"/>.
        /// </summary>
        public string? V2TableName { get; set; }

        /// <summary>
        /// Gets or sets the schema migration mode. The default is <see cref="DynamoDBReminderTableMode.Legacy"/>.
        /// </summary>
        public DynamoDBReminderTableMode TableMode { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of legacy items evaluated by each resumable backfill scan page.
        /// </summary>
        public int MigrationPageSize { get; set; } = 100;
    }
}
