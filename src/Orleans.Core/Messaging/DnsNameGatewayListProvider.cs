using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// This Gateway list provider looks up ip addresses based on a dns
    /// name.  This is ideal for container environments (kubernetes, swarm)
    /// as well as Service Fabric using the dns feature.
    /// </summary>
    public class DnsNameGatewayListProvider : IGatewayListProvider
    {
        private readonly DnsNameGatewayListProviderOptions options;
        private readonly TimeSpan maxStaleness;

        public DnsNameGatewayListProvider(IOptions<DnsNameGatewayListProviderOptions> options, IOptions<GatewayOptions> gatewayOptions)
        {
            this.options = options.Value;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider() => Task.CompletedTask;


        public Task<IList<Uri>> GetGateways()
        {
            var endpointUris = Dns.GetHostEntry(this.options.DnsName)
                                            .AddressList
                                            .Select(a => new IPEndPoint(a, this.options.Port).ToGatewayUri())
                                            .ToList();

            return Task.FromResult<IList<Uri>>(endpointUris);
        }

        public TimeSpan MaxStaleness
        {
            get => this.maxStaleness;
        }

        public bool IsUpdatable
        {
            get => true;
        }

        #endregion
    }
}
