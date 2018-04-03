using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Configuration.Validators
{
    internal class ClientClusteringValidator : IConfigurationValidator
    {
        internal const string ClusteringNotConfigured =
            "Clustering has not been configured. Configure clustering using one of the clustering packages, such as:"
            + "\n  * Microsoft.Orleans.Clustering.AzureStorage"
            + "\n  * Microsoft.Orleans.Clustering.AdoNet for ADO.NET systems such as SQL Server, MySQL, PostgreSQL, and Oracle"
            + "\n  * Microsoft.Orleans.Clustering.DynamoDB"
            + "\n  * Microsoft.Orleans.Clustering.ServiceFabric"
            + "\n  * Microsoft.Orleans.Clustering.Consul"
            + "\n  * Microsoft.Orleans.Clustering.ZooKeeper"
            + "\n  * Others, see: https://www.nuget.org/packages?q=Microsoft.Orleans.Clustering.";

        internal static readonly string ClusterIdNotConfigured =
            $"A cluster id has not been configured. Configure a cluster id by specifying a value for {nameof(ClusterOptions)}.{nameof(ClusterOptions.ClusterId)}." +
            " For more information, please see the documentation on:" +
            "\n  * Client Configuration: http://dotnet.github.io/orleans/Documentation/Deployment-and-Operations/Configuration-Guide/Client-Configuration.html" +
            "\n  * Server Configuration: http://dotnet.github.io/orleans/Documentation/Deployment-and-Operations/Configuration-Guide/Server-Configuration.html";

        private readonly IServiceProvider serviceProvider;

        public ClientClusteringValidator(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public void ValidateConfiguration()
        {
            var clusterOptions = this.serviceProvider.GetRequiredService<IOptions<ClusterOptions>>();
            if (string.IsNullOrWhiteSpace(clusterOptions.Value.ClusterId))
            {
                throw new OrleansConfigurationException(ClusterIdNotConfigured);
            }

            var gatewayProvider = this.serviceProvider.GetService<IGatewayListProvider>();
            if (gatewayProvider == null)
            {
                throw new OrleansConfigurationException(ClusteringNotConfigured);
            }
        }
    }
}
