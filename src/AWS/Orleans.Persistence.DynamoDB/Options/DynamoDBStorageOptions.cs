using System;
using Newtonsoft.Json;
using Orleans.Persistence.DynamoDB;

namespace Orleans.Configuration
{
    public class DynamoDBStorageOptions
    {
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        [Redact]
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for DynamoDB storage
        /// </summary>
        [Redact]
        public string SecretKey { get; set; }

        /// <summary>
        /// DynamoDB Service name
        /// </summary>
        public string Service { get; set; }

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
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansGrainState'.
        /// </summary>
        public string TableName { get; set; } = "OrleansGrainState";

        /// <summary>
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DefaultInitStage;
        public const int DefaultInitStage = ServiceLifecycleStage.ApplicationServices;

        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }

        public StateCompressionPolicy StateCompressionPolicy { get; set; } = null;

        /// <summary>
        /// Specifies a time span in which the item would be expired in the future
        /// every StateWrite will increase the TTL of the grain
        /// </summary>
        public TimeSpan? TimeToLive { get; set; }
    }
}
