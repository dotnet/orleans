using Microsoft.Extensions.Logging;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal interface IStorageProvider
    {
        IDynamoDBStorage GetConfStorage(ILoggerFactory loggerFactory);

        IDynamoDBStorage GetGatewayStorage(ILoggerFactory loggerFactory);
    }
}