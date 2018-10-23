﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Consul;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Membership
{
    public class ConsulGatewayListProvider : IGatewayListProvider
    {
        private ConsulClient consulClient;
        private string clusterId;
        private ILogger logger;
        private readonly ConsulClusteringClientOptions options;
        private readonly TimeSpan maxStaleness;
        private readonly string kvRootFolder;

        public ConsulGatewayListProvider(
            ILogger<ConsulGatewayListProvider> logger, 
            IOptions<ConsulClusteringClientOptions> options, 
            IOptions<GatewayOptions> gatewayOptions, 
            IOptions<ClusterOptions> clusterOptions)
        {
            this.logger = logger;
            this.clusterId = clusterOptions.Value.ClusterId;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
            this.options = options.Value;
            this.kvRootFolder = options.Value.KvRootFolder;
        }

        public TimeSpan MaxStaleness
        {
            get { return this.maxStaleness; }
        }

        public Boolean IsUpdatable
        {
            get { return true; }
        }
        public Task InitializeGatewayListProvider()
        {
            consulClient =
                new ConsulClient(config =>
                {
                    config.Address = options.Address;
                    config.Token = options.AclClientToken;
                });

            return Task.CompletedTask;
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ConsulBasedMembershipTable.ReadAll(this.consulClient, this.clusterId, this.kvRootFolder, this.logger);
            if (membershipTableData == null) return new List<Uri>();

            return membershipTableData.Members.Select(e => e.Item1).
                Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                Select(m =>
                {
                    var endpoint = new IPEndPoint(m.SiloAddress.Endpoint.Address, m.ProxyPort);
                    var gatewayAddress = SiloAddress.New(endpoint, m.SiloAddress.Generation);
                    return gatewayAddress.ToGatewayUri();
                }).ToList();
        }
    }


}
