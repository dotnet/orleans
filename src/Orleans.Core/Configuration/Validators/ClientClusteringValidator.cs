﻿using System;

using Microsoft.Extensions.DependencyInjection;

using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Configuration.Validators
{
    internal class ClientClusteringValidator : IConfigurationValidator
    {
        internal const string ClusteringNotConfigured =
            "Clustering has not been configured. Configure clustering using one of the clustering packages, such as: "
            + "\n  * Microsoft.Orleans.Clustering.AzureStorage for Microsoft Azure Storage"
            + "\n  * Microsoft.Orleans.Clustering.AdoNet for ADO.NET systems such as SQL Server, MySQL, PostgreSQL, and Oracle"
            + "\n  * Microsoft.Orleans.Clustering.DynamoDB"
            + "\n  * Microsoft.Orleans.Clustering.ServiceFabric"
            + "\n  * See: https://www.nuget.org/packages?q=orleans+clustering";

        private readonly IServiceProvider serviceProvider;

        public ClientClusteringValidator(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public void ValidateConfiguration()
        {
            var gatewayProvider = this.serviceProvider.GetService<IGatewayListProvider>();
            if (gatewayProvider == null)
            {
                throw new OrleansConfigurationException(ClusteringNotConfigured);
            }
        }
    }
}
