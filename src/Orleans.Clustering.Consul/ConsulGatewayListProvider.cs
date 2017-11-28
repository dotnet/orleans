﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ConsulUtils.Options;

namespace Orleans.Runtime.Membership
{
    public class ConsulGatewayListProvider : IGatewayListProvider
    {
        private ConsulClient consulClient;
        private string deploymentId;
        private ILogger logger;
        private readonly ConsulGatewayListProviderOptions options;
        private readonly TimeSpan maxStaleness;
        public ConsulGatewayListProvider(ILogger<ConsulGatewayListProvider> logger, ClientConfiguration clientConfig, IOptions<ConsulGatewayListProviderOptions> options)
        {
            this.logger = logger;
            this.deploymentId = clientConfig.DeploymentId;
            this.maxStaleness = clientConfig.GatewayListRefreshPeriod;
            this.options = options.Value;
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
                new ConsulClient(config => config.Address = options.Address);

            return Task.CompletedTask;
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ConsulBasedMembershipTable.ReadAll(this.consulClient, this.deploymentId, this.logger);
            if (membershipTableData == null) return new List<Uri>();

            return membershipTableData.Members.Select(e => e.Item1).
                Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                Select(m =>
                {
                    m.SiloAddress.Endpoint.Port = m.ProxyPort;
                    return m.SiloAddress.ToGatewayUri();
                }).ToList();
        }
    }


}
