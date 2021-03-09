using Orleans.Clustering.DynamoDB;

namespace Orleans.Configuration
{
    public class DynamoDBGatewayOptions : DynamoDBClientOptions
    {
        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansSilos'.
        /// </summary>
        public string TableName { get; set; } = "OrleansSilos";
    }
}
