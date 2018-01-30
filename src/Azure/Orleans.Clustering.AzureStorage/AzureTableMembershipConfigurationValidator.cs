using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Clustering.AzureStorage
{
    // Validator to validate Azure table membership settings
    public class AzureTableMembershipConfigurationValidator : IConfigurationValidator
    {
        private GlobalConfiguration configuration;

        public AzureTableMembershipConfigurationValidator(GlobalConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public void ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(this.configuration.ClusterId))
            {
                throw new OrleansConfigurationException($"Invalid Configuration. ClusterId value is required.");
            }
        }
    }
}
