using Orleans.Clustering.DynamoDB;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures DynamoDB-backed silo membership.
    /// </summary>
    public class DynamoDBClusteringOptions : DynamoDBClientOptions
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
        /// Defaults to 'OrleansSilos'.
        /// </summary>
        public string TableName { get; set; } = "OrleansSilos";
    }

    internal sealed class DynamoDBClusteringOptionsValidator(DynamoDBClusteringOptions options) : IConfigurationValidator
    {
        public void ValidateConfiguration()
            => DynamoDBProviderConfiguration.ValidateTableOptions(
                options,
                options.TableName,
                options.UseProvisionedThroughput,
                options.ReadCapacityUnits,
                options.WriteCapacityUnits,
                "DynamoDB clustering");
    }
}
