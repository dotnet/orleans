using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using OrleansZooKeeperUtils.Configuration;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
    public class LegacyZooKeeperMembershipConfigurator : ILegacyMembershipConfigurator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly GlobalConfiguration globalConfiguration;
        public LegacyZooKeeperMembershipConfigurator(GlobalConfiguration globalConfiguration,
            ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            this.globalConfiguration = globalConfiguration;
        }

        /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
        public IMembershipTable Configure()
        {
            var options = Options.Create(new ZooKeeperMembershipOptions()
            {
                ConnectionString = globalConfiguration.DataConnectionString,
            });
            return new ZooKeeperBasedMembershipTable(this.loggerFactory.CreateLogger<ZooKeeperBasedMembershipTable>(), options, this.globalConfiguration);
        }
    }
}
