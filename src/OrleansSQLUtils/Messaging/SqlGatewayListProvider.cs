﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Orleans.SqlUtils;
using Orleans.Runtime;
using OrleansSQLUtils.Options;

namespace Orleans.Runtime.Membership
{
    public class SqlGatewayListProvider : IGatewayListProvider
    {
        private readonly ILogger logger;
        private string deploymentId;
        private readonly SqlGatewayProviderOptions options;
        private RelationalOrleansQueries orleansQueries;
        private readonly IGrainReferenceConverter grainReferenceConverter;

        public SqlGatewayListProvider(ILogger<SqlGatewayListProvider> logger, IGrainReferenceConverter grainReferenceConverter, ClientConfiguration clientConfiguration,
            IOptions<SqlGatewayProviderOptions> options)
        {
            this.logger = logger;
            this.grainReferenceConverter = grainReferenceConverter;
            this.options = options.Value;
            deploymentId = clientConfiguration.DeploymentId;
        }

        public TimeSpan MaxStaleness
        {
            get { return this.options.GatewayListRefreshPeriod; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        public async Task InitializeGatewayListProvider()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("SqlMembershipTable.InitializeGatewayListProvider called.");
            orleansQueries = await RelationalOrleansQueries.CreateInstance(options.AdoInvariant, options.ConnectionString, this.grainReferenceConverter);
        }

        public async Task<IList<Uri>> GetGateways()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("SqlMembershipTable.GetGateways called.");
            try
            {
                return await orleansQueries.ActiveGatewaysAsync(deploymentId);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.Gateways failed {0}", ex);
                throw;
            }
        }
    }
}
