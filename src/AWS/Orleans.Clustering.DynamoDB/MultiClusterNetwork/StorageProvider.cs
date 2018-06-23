using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal class StorageProvider : IStorageProvider
    {
        private readonly DynamoDBClusteringOptions _options;

        public StorageProvider(string connectionString)
        {
            _options = new DynamoDBClusteringOptions();
            LegacyDynamoDBMembershipConfigurator.ParseDataConnectionString(connectionString, _options);
        }

        public IDynamoDBStorage GetConfStorage(ILoggerFactory loggerFactory)
        {
            return new DynamoDBStorage(loggerFactory, _options.Service, _options.AccessKey, _options.SecretKey,
                _options.ReadCapacityUnits, _options.WriteCapacityUnits);
        }

        public IDynamoDBStorage GetGatewayStorage(ILoggerFactory loggerFactory)
        {
            return  new DynamoDBStorage(loggerFactory, _options.Service, _options.AccessKey, _options.SecretKey,
                _options.ReadCapacityUnits, _options.WriteCapacityUnits);
        }
    }
}
