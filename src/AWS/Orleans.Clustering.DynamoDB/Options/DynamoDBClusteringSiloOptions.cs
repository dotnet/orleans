namespace Orleans.Configuration
{
    public class DynamoDBClusteringSiloOptions
    {
        /// <summary>
        /// Connection string for DynamoDB Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }
    }
}
