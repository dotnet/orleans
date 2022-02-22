using System;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Configuration.Validators
{
    /// <summary>
    /// Validator for client-side clustering.
    /// </summary>
    internal class ClientClusteringValidator : IConfigurationValidator
    {
        /// <summary>
        /// The error message displayed when clustering is misconfigured.
        /// </summary>
        internal const string ClusteringNotConfigured =
            "Clustering has not been configured. Configure clustering using one of the clustering packages, such as:"
            + "\n  * Microsoft.Orleans.Clustering.AzureStorage"
            + "\n  * Microsoft.Orleans.Clustering.AdoNet for ADO.NET systems such as SQL Server, MySQL, PostgreSQL, and Oracle"
            + "\n  * Microsoft.Orleans.Clustering.DynamoDB"
            + "\n  * Microsoft.Orleans.Clustering.ServiceFabric"
            + "\n  * Microsoft.Orleans.Clustering.Consul"
            + "\n  * Microsoft.Orleans.Clustering.ZooKeeper"
            + "\n  * Others, see: https://www.nuget.org/packages?q=Microsoft.Orleans.Clustering.";

        /// <summary>
        /// The service provider.
        /// </summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientClusteringValidator"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
        public ClientClusteringValidator(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            var gatewayProvider = _serviceProvider.GetService<IGatewayListProvider>();
            if (gatewayProvider == null)
            {
                throw new OrleansConfigurationException(ClusteringNotConfigured);
            }
        }
    }
}
