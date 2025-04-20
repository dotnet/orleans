using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Messaging;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership
{
    public partial class AdoNetGatewayListProvider : IGatewayListProvider
    {
        private readonly ILogger _logger;
        private readonly string _clusterId;
        private readonly AdoNetClusteringClientOptions _options;
        private RelationalOrleansQueries _orleansQueries;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _maxStaleness;

        public AdoNetGatewayListProvider(
            ILogger<AdoNetGatewayListProvider> logger,
            IServiceProvider serviceProvider,
            IOptions<AdoNetClusteringClientOptions> options,
            IOptions<GatewayOptions> gatewayOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this._logger = logger;
            this._serviceProvider = serviceProvider;
            this._options = options.Value;
            this._clusterId = clusterOptions.Value.ClusterId;
            this._maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        public TimeSpan MaxStaleness
        {
            get { return this._maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        public async Task InitializeGatewayListProvider()
        {
            LogTraceInitializeGatewayListProvider();
            _orleansQueries = await RelationalOrleansQueries.CreateInstance(_options.Invariant, _options.ConnectionString);
        }

        public async Task<IList<Uri>> GetGateways()
        {
            LogTraceGetGateways();
            try
            {
                return await _orleansQueries.ActiveGatewaysAsync(this._clusterId);
            }
            catch (Exception ex)
            {
                LogDebugGatewaysFailed(ex);
                throw;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetGatewayListProvider)}.{nameof(InitializeGatewayListProvider)} called."
        )]
        private partial void LogTraceInitializeGatewayListProvider();

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetGatewayListProvider)}.{nameof(GetGateways)} called."
        )]
        private partial void LogTraceGetGateways();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetGatewayListProvider)}.{nameof(GetGateways)} failed"
        )]
        private partial void LogDebugGatewaysFailed(Exception exception);
    }
}
