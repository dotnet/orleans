using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using OrleansSQLUtils.Configuration;

namespace OrleansSQLUtils
{
    /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
    public class LegacySqlMembershipConfigurator : ILegacyMembershipConfigurator
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly GlobalConfiguration globalConfiguration;
        private readonly IGrainReferenceConverter converter;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="globalConfiguration"></param>
        /// <param name="loggerFactory"></param>
        /// <param name="converter"></param>
        public LegacySqlMembershipConfigurator(GlobalConfiguration globalConfiguration, ILoggerFactory loggerFactory, IGrainReferenceConverter converter)
        {
            this.loggerFactory = loggerFactory;
            this.globalConfiguration = globalConfiguration;
            this.converter = converter;
        }
        /// <inheritdoc cref="ILegacyMembershipConfigurator"/>
        public IMembershipTable Configure()
        {
            var options = Options.Create(new SqlMembershipOptions()
            {
                ConnectionString = globalConfiguration.DataConnectionString,
                AdoInvariant = globalConfiguration.AdoInvariant
            });
            return new SqlMembershipTable(this.converter, globalConfiguration, options, loggerFactory.CreateLogger<SqlMembershipTable>());
        }
    }
}
