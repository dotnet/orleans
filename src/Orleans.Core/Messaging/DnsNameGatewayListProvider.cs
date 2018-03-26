using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;

        public DnsNameGatewayListProvider(ILoggerFactory loggerFactory, IOptions<DnsNameGatewayListProviderOptions> options, IOptions<GatewayOptions> gatewayOptions)
        {
            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger<DnsNameGatewayListProvider>();
            this.options = options.Value;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider() => Task.CompletedTask;


        public Task<IList<Uri>> GetGateways()
        {
            try
            {
                var endpointUris = Dns.GetHostEntry(this.options.DnsName)
                                                .AddressList
                                                .Select(a => new IPEndPoint(a, this.options.Port).ToGatewayUri())
                                                .OrderBy(a => a.AbsoluteUri)
                                                .ToList();

                return Task.FromResult<IList<Uri>>(endpointUris);
            }
            catch (System.Net.Sockets.SocketException se)
            {
                if (se.Message == "No such host is known")
                {
                    logger.Warn(ErrorCode.ProxyClient_GetGateways, $"No addresses found for silo gateways with DNS hostname: {this.options.DnsName}");
                    return Task.FromResult<IList<Uri>>(new List<Uri>());
                }
                throw;
            }
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
