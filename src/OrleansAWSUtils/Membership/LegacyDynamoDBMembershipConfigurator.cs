using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;
using OrleansAWSUtils.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
    public class LegacyDynamoDBMembershipConfigurator : ILegacyMembershipConfigurator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly GlobalConfiguration globalConfiguration;
        public LegacyDynamoDBMembershipConfigurator(GlobalConfiguration globalConfiguration, ILoggerFactory loggerFactory)
        {
            this.globalConfiguration = globalConfiguration;
            
            this.loggerFactory = loggerFactory;
        }

        /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
        public IMembershipTable Configure()
        {
            var options = Options.Create(new DynamoDBMembershipOptions()
            {
                ConnectionString = globalConfiguration.DataConnectionString
            });
            return new DynamoDBMembershipTable(this.loggerFactory, options, this.globalConfiguration);
        }
    }
}
