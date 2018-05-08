using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    class DynamoDBBasedGossipChannel : IGossipChannel
    {
        private readonly ILogger logger;
        private static int _sequenceNumber;
        private GossipTableInstanceManager _tableManager;

        public string Name { get; }
        private readonly ILoggerFactory _loggerFactory;

        public DynamoDBBasedGossipChannel(ILoggerFactory loggerFactory)
        {
            Name = "DynamoDBBasedGossipChannel-" + ++_sequenceNumber;
            logger = loggerFactory.CreateLogger<DynamoDBBasedGossipChannel>();
            _loggerFactory = loggerFactory;
        }

        public async Task Initialize(string serviceId, string connectionString)
        {
            logger.Info("Initializing Gossip Channel for ServiceId={0} using connection: {1}",
                serviceId, ConfigUtilities.RedactConnectionStringInfo(connectionString));
            
            _tableManager = await GossipTableInstanceManager.GetManager(serviceId, connectionString, _loggerFactory);
        }

        public async Task Publish(IMultiClusterGossipData data)
        {
            logger.Debug("-Publish data:{0}", data);
            // this is (almost) always called with just one item in data to be written back
            // so we are o.k. with doing individual tasks for each storage read and write

            if (data.Configuration != null)
            {
                var configInStorage = await _tableManager.ReadConfigurationEntryAsync();
                await DiffAndWriteBackConfigAsync(data.Configuration, configInStorage);
            }
            foreach (var gateway in data.Gateways.Values)
            {
                var gatewayInfoInStorage = await _tableManager.ReadGatewayEntryAsync(gateway);
                await DiffAndWriteBackGatewayInfoAsync(gateway, gatewayInfoInStorage);
            }
        }

        // compare config with configInStorage, and
        // - write config to storage if it is newer (or do nothing on etag conflict)
        // - return config from store if it is newer
        internal async Task<MultiClusterConfiguration> DiffAndWriteBackConfigAsync(MultiClusterConfiguration config, GossipConfiguration configInStorage)
        {
            if (config != null &&
                (configInStorage == null || configInStorage.GossipTimestamp < config.AdminTimestamp))
            {
                // push the more recent configuration to storage
                if (configInStorage == null)
                    await _tableManager.TryCreateConfigurationEntryAsync(config);
                else
                    await _tableManager.TryUpdateConfigurationEntryAsync(config, configInStorage);
            }
            else if (configInStorage != null &&
                     (config == null || config.AdminTimestamp < configInStorage.GossipTimestamp))
            {
                // pull the more recent configuration from storage
                return configInStorage.ToConfiguration();
            }
            return null;
        }

        // compare gatewayInfo with gatewayInfoInStorage, and
        // - write gatewayInfo to storage if it is newer (or do nothing on etag conflict)
        // - remove expired gateway info from storage
        // - return gatewayInfoInStorage if it is newer
        internal async Task<GatewayEntry> DiffAndWriteBackGatewayInfoAsync(GatewayEntry gatewayInfo, GossipGateway gatewayInfoInStorage)
        {
            if ((gatewayInfo != null && !gatewayInfo.Expired)
                && (gatewayInfoInStorage == null || gatewayInfoInStorage.GossipTimestamp < gatewayInfo.HeartbeatTimestamp))
            {
                // push  the more recent gateway info to storage
                if (gatewayInfoInStorage == null)
                {
                    await _tableManager.TryCreateGatewayEntryAsync(gatewayInfo);
                }
                else
                {
                    await _tableManager.TryUpdateGatewayEntryAsync(gatewayInfo, gatewayInfoInStorage, gatewayInfoInStorage.Version);
                }
            }
            else if (gatewayInfoInStorage != null &&
                     (gatewayInfo == null || gatewayInfo.HeartbeatTimestamp < gatewayInfoInStorage.GossipTimestamp))
            {
                var fromstorage = gatewayInfoInStorage.ToGatewayEntry();
                if (fromstorage.Expired)
                {
                    // remove gateway info from storage
                    await _tableManager.TryDeleteGatewayEntryAsync(gatewayInfoInStorage, gatewayInfoInStorage.Version);
                }
                else
                {
                    // pull the more recent info from storage
                    return fromstorage;
                }
            }
            return null;
        }

        public async Task<IMultiClusterGossipData> Synchronize(IMultiClusterGossipData gossipdata)
        {
            throw new NotImplementedException();
        }
    }
}
