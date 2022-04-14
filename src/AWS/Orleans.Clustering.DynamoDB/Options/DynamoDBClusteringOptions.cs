using Orleans.Clustering.DynamoDB;

namespace Orleans.Configuration
{
    public class DynamoDBClusteringOptions
    {
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
        /// Defaults to 'OrleansSilos'.
        /// </summary>
        public string TableName { get; set; } = "OrleansSilos";
    }
}
