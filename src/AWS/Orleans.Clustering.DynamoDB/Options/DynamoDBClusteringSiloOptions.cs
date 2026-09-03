namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the DynamoDB connection used by a silo for clustering.
    /// </summary>
    public class DynamoDBClusteringSiloOptions
    {
        /// <summary>
        /// Connection string for DynamoDB Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; } = null!;
    }
}
