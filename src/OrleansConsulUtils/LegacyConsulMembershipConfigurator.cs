using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.ConsulUtils.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Membership;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
    public class LegacyConsulMembershipConfigurator : ILegacyMembershipConfigurator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly GlobalConfiguration globalConfiguration;
        public LegacyConsulMembershipConfigurator(ILoggerFactory loggerFactory, GlobalConfiguration globalConfiguration)
        {
            this.globalConfiguration = globalConfiguration;
            this.loggerFactory = loggerFactory;
        }

        /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
        public IMembershipTable Configure()
        {
            var options = Options.Create(new ConsulMembershipOptions()
            {
                ConnectionString = globalConfiguration.DataConnectionString
            });
            return new ConsulBasedMembershipTable(loggerFactory.CreateLogger<ConsulBasedMembershipTable>(), options, globalConfiguration);
        }
    }
}
