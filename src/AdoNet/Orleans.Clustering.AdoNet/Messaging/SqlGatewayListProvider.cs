using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using OrleansSQLUtils.Options;

namespace Orleans.Runtime.Membership
{
    public class SqlGatewayListProvider : IGatewayListProvider
    {
        private readonly ILogger logger;
        private string clusterId;
        private readonly SqlGatewayListProviderOptions options;
        private RelationalOrleansQueries orleansQueries;
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private readonly TimeSpan maxStaleness;
        public SqlGatewayListProvider(ILogger<SqlGatewayListProvider> logger, IGrainReferenceConverter grainReferenceConverter, ClientConfiguration clientConfiguration,
            IOptions<SqlGatewayListProviderOptions> options,
            IOptions<ClusterClientOptions> clusterClientOptions)
        {
            this.logger = logger;
            this.grainReferenceConverter = grainReferenceConverter;
            this.options = options.Value;
            this.clusterId = clusterClientOptions.Value.ClusterId;
            this.maxStaleness = clientConfiguration.GatewayListRefreshPeriod;
        }

        public TimeSpan MaxStaleness
        {
            get { return this.maxStaleness; }
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
                return await orleansQueries.ActiveGatewaysAsync(this.clusterId);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("SqlMembershipTable.Gateways failed {0}", ex);
                throw;
            }
        }
    }
}
