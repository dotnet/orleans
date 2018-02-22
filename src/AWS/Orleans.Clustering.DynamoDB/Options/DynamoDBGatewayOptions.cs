using Orleans.Clustering.DynamoDB;

namespace Orleans.Configuration
{
    public class DynamoDBGatewayOptions
    {
        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for DynamoDB storage
        /// </summary>
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
        /// DynamoDB table name.
        /// Defaults to 'OrleansSilos'.
        /// </summary>
        public string TableName { get; set; } = "OrleansSilos";
    }
}
