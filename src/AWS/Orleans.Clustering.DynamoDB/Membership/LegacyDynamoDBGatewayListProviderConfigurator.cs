using System;

using Microsoft.Extensions.DependencyInjection;

using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;

namespace OrleansAWSUtils.Membership
{
    /// <inheritdoc/>
    public class LegacyDynamoDBGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.Configure<DynamoDBClusteringClientOptions>(
                options =>
                {
                    var reader = new ClientConfigurationReader(configuration);
                    ParseDataConnectionString(reader.GetPropertyValue<string>("DataConnectionString"), options);
                });

            services.AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
        }

        /// <summary>
        /// Parse data connection string to fill in fields in <paramref name="options"/>
        /// </summary>
        /// <param name="dataConnectionString"></param>
        /// <param name="options"></param>
        public static void ParseDataConnectionString(string dataConnectionString, DynamoDBClusteringClientOptions options)
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
