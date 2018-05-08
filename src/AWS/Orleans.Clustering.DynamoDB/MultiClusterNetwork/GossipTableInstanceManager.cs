using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    class GossipTableInstanceManager
    {
        private const string CONF_TABLE_NAME = "OrleansGossipConfigurationTable";
        private const string GATEWAY_TABLE_NAME = "OrleansGossipGatewayTable";

        private readonly string _globalServiceId;
        private readonly DynamoDBStorage _confStorage;
        private readonly DynamoDBStorage _gatewayStorage;
        private readonly ILogger _logger;

        private GossipTableInstanceManager(string globalServiceId, string storageConnectionString, ILoggerFactory loggerFactory)
        {
            _globalServiceId = globalServiceId;

            _logger = loggerFactory.CreateLogger<GossipTableInstanceManager>();

            // Using legacy here
            var options = new DynamoDBClusteringOptions();
            LegacyDynamoDBMembershipConfigurator.ParseDataConnectionString(storageConnectionString, options);

            _confStorage = new DynamoDBStorage(loggerFactory, options.Service, options.AccessKey, options.SecretKey,
                options.ReadCapacityUnits, options.WriteCapacityUnits);
            _gatewayStorage = new DynamoDBStorage(loggerFactory, options.Service, options.AccessKey, options.SecretKey,
                options.ReadCapacityUnits, options.WriteCapacityUnits);
        }

        public static async Task<GossipTableInstanceManager> GetManager(string globalServiceId, string storageConnectionString, ILoggerFactory loggerFactory)
        {
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            var instance = new GossipTableInstanceManager(globalServiceId, storageConnectionString, loggerFactory);

            await InitializeTableAsync(instance._confStorage, CONF_TABLE_NAME);
            await InitializeTableAsync(instance._gatewayStorage, GATEWAY_TABLE_NAME);

            return instance;
        }

        private static async Task InitializeTableAsync(DynamoDBStorage storage, string tableName)
        {
            try
            {
                await storage.InitializeTable(tableName, null, null).ConfigureAwait(false);
            }
            catch (TimeoutException te)
            {
                var errorMsg = $"Unable to create or connect to the DynamoDB table {tableName}";
                throw new OrleansException(errorMsg, te);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Exception trying to create or connect to DynamoDB table {tableName} : {ex.Message}";
                throw new OrleansException(errorMsg, ex);
            }
        }

        #region Configuration

        public async Task<GossipConfiguration> ReadConfigurationEntryAsync()
        {
            var keys = new Dictionary<string, AttributeValue>
            {
                ["ServiceId"] = new AttributeValue(_globalServiceId)
            };

            return await _confStorage.ReadSingleEntryAsync(CONF_TABLE_NAME, keys, fields => new GossipConfiguration(fields)).ConfigureAwait(false);
        }

        public async Task TryCreateConfigurationEntryAsync(MultiClusterConfiguration config)
        {
            var conf = new GossipConfiguration(config)
            {
                Version = 0
            };

            await _confStorage.PutEntryAsync(CONF_TABLE_NAME, conf.ToAttributes()).ConfigureAwait(false);
        }

        public async Task<bool> TryUpdateConfigurationEntryAsync(MultiClusterConfiguration configuration, GossipConfiguration configInStorage)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");

            configInStorage.GossipTimestamp = configuration.AdminTimestamp;
            configInStorage.Clusters = configuration.Clusters.ToList();
            configInStorage.Comment = configuration.Comment ?? "";
            configInStorage.Version = ++configInStorage.Version;

            return (await TryUpdateTableEntryAsync(configInStorage).ConfigureAwait(false));
        }

        /// <summary>
        /// Try once to conditionally update a data entry in the Azure table. Returns false if etag does not match.
        /// </summary>
        private async Task<bool> TryUpdateTableEntryAsync(GossipConfiguration data, [CallerMemberName]string operation = null)
        {
            var keys = new Dictionary<string, AttributeValue>
            {
                ["ServiceId"] = new AttributeValue(_globalServiceId),
            };

            return await TryOperation(() => _confStorage.UpsertEntryAsync(CONF_TABLE_NAME, keys, data.ToAttributes(), "", null, null));
        }

        #endregion

        #region Gateway

        public async Task<GossipGateway> ReadGatewayEntryAsync(GatewayEntry gateway)
        {
            var gw = new GossipGateway(gateway, _globalServiceId);

            return await _gatewayStorage.ReadSingleEntryAsync(GATEWAY_TABLE_NAME, gw.ToKeyAttributes(), fields => new GossipGateway(fields)).ConfigureAwait(false);
        }

        public async Task TryCreateGatewayEntryAsync(GatewayEntry gatewayInfo)
        {
            var gw = new GossipGateway(gatewayInfo, _globalServiceId);

            await _gatewayStorage.PutEntryAsync(GATEWAY_TABLE_NAME, gw.ToAttributes()).ConfigureAwait(false);
        }

        public async Task TryDeleteGatewayEntryAsync(GossipGateway gatewayInfoInStorage)
        {
            await _gatewayStorage.DeleteEntryAsync(GATEWAY_TABLE_NAME, gatewayInfoInStorage.ToKeyAttributes(),
                  conditionValues: gatewayInfoInStorage.ToConditionalAttributes());
        }

        public async Task TryUpdateGatewayEntryAsync(GatewayEntry gatewayInfo, GossipGateway gatewayInfoInStorage)
        {
            var gw = new GossipGateway(gatewayInfo, _globalServiceId) { Version = gatewayInfoInStorage.Version };

            await _gatewayStorage.UpsertEntryAsync(GATEWAY_TABLE_NAME, gw.ToKeyAttributes(), gw.ToAttributes(true),
                conditionValues: gw.ToConditionalAttributes());
        }

        #endregion

        private async Task<bool> TryOperation(Func<Task> func, string operation = null)
        {
            try
            {
                await func().ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                // todo: improve logged message by parsing AWS specific exceptions
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.Trace("{0} failed", operation);

                throw;
            }
        }
    }
}
