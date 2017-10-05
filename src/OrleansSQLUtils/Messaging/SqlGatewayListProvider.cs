using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Orleans.SqlUtils;
using Orleans.Runtime;

namespace Orleans.Runtime.Membership
{
    public class SqlGatewayListProvider : IGatewayListProvider
    {
        private readonly ILogger logger;
        private string deploymentId;
        private TimeSpan maxStaleness;
        private RelationalOrleansQueries orleansQueries;
        private readonly IGrainReferenceConverter grainReferenceConverter;

        public SqlGatewayListProvider(ILogger<SqlGatewayListProvider> logger, IGrainReferenceConverter grainReferenceConverter)
        {
            this.logger = logger;
            this.grainReferenceConverter = grainReferenceConverter;
        }

        public TimeSpan MaxStaleness
        {
            get { return maxStaleness; }
        }


        public bool IsUpdatable
        {
            get { return true; }
        }

        public async Task InitializeGatewayListProvider(ClientConfiguration config)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("SqlMembershipTable.InitializeGatewayListProvider called.");

            deploymentId = config.DeploymentId;
            maxStaleness = config.GatewayListRefreshPeriod;
            orleansQueries = await RelationalOrleansQueries.CreateInstance(config.AdoInvariant, config.DataConnectionString, this.grainReferenceConverter);
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
