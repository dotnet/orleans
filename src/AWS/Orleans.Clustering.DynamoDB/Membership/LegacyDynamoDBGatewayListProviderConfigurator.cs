using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.DynamoDB;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using OrleansAWSUtils.Options;

namespace OrleansAWSUtils.Membership
{
    /// <inheritdoc/>
    public class LegacyDynamoDBGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseDynamoDBGatewayListProvider(options =>
            {
               ParseDataConnectionString(configuration.DataConnectionString, options);
            });
        }

        /// <summary>
        /// Parse data connection string to fill in fields in <paramref name="options"/>
        /// </summary>
        /// <param name="dataConnectionString"></param>
        /// <param name="options"></param>
        public static void ParseDataConnectionString(string dataConnectionString, DynamoDBGatewayListProviderOptions options)
        {
            DynamoDBStorage.ParseDataConnectionString(dataConnectionString, out var accessKey, out var secretKey, out var service, out var readCapacityUnits, out var writeCapacityUnits);
            options.AccessKey = accessKey;
            options.Service = service;
            options.SecretKey = secretKey;
            options.ReadCapacityUnits = readCapacityUnits;
            options.WriteCapacityUnits = writeCapacityUnits;
        }
    }
}
