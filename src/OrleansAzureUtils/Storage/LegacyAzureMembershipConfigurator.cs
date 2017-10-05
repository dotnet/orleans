using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.AzureUtils.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
    public class LegacyAzureTableMembershipConfigurator : ILegacyMembershipConfigurator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly GlobalConfiguration globalConfiguration;
        public LegacyAzureTableMembershipConfigurator(GlobalConfiguration globalConfiguration, ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            this.globalConfiguration = globalConfiguration;
        }

        /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
        public IMembershipTable Configure()
        {
            var membershipOptions = Options.Create(new AzureTableMembershipOptions()
            {
                MaxStorageBusyRetries = globalConfiguration.MaxStorageBusyRetries,
                ConnectionString = globalConfiguration.DataConnectionString
            });
            return new AzureBasedMembershipTable(this.loggerFactory, membershipOptions, globalConfiguration);
        }
    }
}
