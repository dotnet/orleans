using Orleans.Clustering.DynamoDB;

namespace OrleansAWSUtils.Options
{
    public class DynamoDBGatewayListProviderOptions
    {
        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for dynamoDB storage
        /// </summary>
        public string SecretKey { get; set; }

        /// <summary>
        /// Service name 
        /// </summary>
        public string Service { get; set; }

        /// <summary>
        /// Read capacity unit for dynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for dynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;
    }
}
