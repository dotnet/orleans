﻿using System;
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
    internal class GossipTableInstanceManager
    {
        private const string CONF_TABLE_NAME = "OrleansGossipConfigurationTable";
        private const string GATEWAY_TABLE_NAME = "OrleansGossipGatewayTable";

        private readonly DynamoDBStorage _confStorage;
        private readonly DynamoDBStorage _gatewayStorage;
        private readonly string _globalServiceId;
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

            await InitializeTableAsync(instance._confStorage, CONF_TABLE_NAME, GossipConfigurationMapper.Keys, GossipConfigurationMapper.Attributes);
            await InitializeTableAsync(instance._gatewayStorage, GATEWAY_TABLE_NAME, GossipGatewayMapper.Keys, GossipGatewayMapper.Attributes);

            return instance;
        }

        private static async Task InitializeTableAsync(DynamoDBStorage storage, string tableName, List<KeySchemaElement> keys, List<AttributeDefinition> attributes)
        {
            try
            {
                await storage.InitializeTable(tableName, keys, attributes).ConfigureAwait(false);
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
            return await _confStorage.ReadSingleEntryAsync(
                CONF_TABLE_NAME,
                GossipConfigurationMapper.ToKeyAttributes(_globalServiceId),
                GossipConfigurationMapper.ToConfiguration).ConfigureAwait(false);
        }

        public async Task TryCreateConfigurationEntryAsync(MultiClusterConfiguration config)
        {
            var conf = new GossipConfiguration(config)
            {
                Version = 0,
                ServiceId = _globalServiceId
            };

            await _confStorage.PutEntryAsync(CONF_TABLE_NAME, conf.ToAttributes()).ConfigureAwait(false);
        }

        public async Task<bool> TryUpdateConfigurationEntryAsync(MultiClusterConfiguration configuration, GossipConfiguration configInStorage, [CallerMemberName]string operation = null)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");

            configInStorage.GossipTimestamp = configuration.AdminTimestamp;
            configInStorage.Clusters = configuration.Clusters.ToList();
            configInStorage.Comment = configuration.Comment ?? "";
            configInStorage.Version = configInStorage.Version;

            return await TryOperation(() => TryUpdateTableEntryAsync(configInStorage), operation);
        }

        /// <summary>
        /// Try once to conditionally update a data entry in the DynamoDB table. Returns false if version does not match.
        /// </summary>
        private async Task<bool> TryUpdateTableEntryAsync(GossipConfiguration data, [CallerMemberName]string operation = null)
        {
            return await TryOperation(() => _confStorage.UpsertEntryAsync(
                CONF_TABLE_NAME,
                GossipConfigurationMapper.ToKeyAttributes(_globalServiceId),
                data.ToAttributes(true),
                GossipConfigurationMapper.ConditionalExpression,
                data.ToConditionalAttributes()), operation);
        }

        #endregion

        #region Gateway

        public async Task<GossipGateway> ReadGatewayEntryAsync(GatewayEntry gateway)
        {
            var gw = new GossipGateway(gateway, _globalServiceId);

            return await _gatewayStorage.ReadSingleEntryAsync(
                GATEWAY_TABLE_NAME,
                gw.ToKeyAttributes(),
                GossipGatewayMapper.ToGateway).ConfigureAwait(false);
        }

        public async Task<Dictionary<SiloAddress, GossipGateway>> ReadGatewayEntriesAsync()
        {
            var result = await _gatewayStorage.QueryAsync(
                GATEWAY_TABLE_NAME,
                GossipGatewayMapper.ToQueryAttributes(_globalServiceId),
                GossipGatewayMapper.QueryExpression,
                GossipGatewayMapper.ToGateway);

            return result.results.ToDictionary(
                r => r.OrleansSiloAddress,
                s => s);
        }

        public async Task TryCreateGatewayEntryAsync(GatewayEntry gatewayInfo)
        {
            var gw = new GossipGateway(gatewayInfo, _globalServiceId) { Version = 0 };

            await _gatewayStorage.PutEntryAsync(GATEWAY_TABLE_NAME, gw.ToAttributes()).ConfigureAwait(false);
        }

        public async Task TryDeleteGatewayEntryAsync(GossipGateway gatewayInfoInStorage)
        {
            await _gatewayStorage.DeleteEntryAsync(
                GATEWAY_TABLE_NAME,
                gatewayInfoInStorage.ToKeyAttributes(),
                GossipGatewayMapper.ConditionalExpression,
                gatewayInfoInStorage.ToConditionalAttributes());
        }

        public async Task TryUpdateGatewayEntryAsync(GatewayEntry gatewayInfo, GossipGateway gatewayInfoInStorage)
        {
            var gw = new GossipGateway(gatewayInfo, _globalServiceId) { Version = gatewayInfoInStorage.Version };

            await _gatewayStorage.UpsertEntryAsync(
                GATEWAY_TABLE_NAME,
                gw.ToKeyAttributes(),
                gw.ToAttributes(true),
                GossipGatewayMapper.ConditionalExpression,
                gw.ToConditionalAttributes());
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
