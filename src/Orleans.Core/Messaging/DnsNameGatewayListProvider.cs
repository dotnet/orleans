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
        private readonly ILogger logger;

        public DnsNameGatewayListProvider(ILoggerFactory loggerFactory, IOptions<DnsNameGatewayListProviderOptions> options, IOptions<GatewayOptions> gatewayOptions)
        {            
            this.logger = loggerFactory.CreateLogger<DnsNameGatewayListProvider>();
            this.options = options.Value;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider() => Task.CompletedTask;


        public async Task<IList<Uri>> GetGateways()
        {
            try
            {
                var addresses = (await Dns.GetHostEntryAsync(this.options.DnsName))
                                                .AddressList
                                                .OrderBy(a => a.ToString())
                                                .ToList();

                var endpointUris = addresses.Select(a => new IPEndPoint(a, this.options.Port).ToGatewayUri())
                                            .ToList();
                
                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    var addressesStr = string.Join("\n", addresses.Select(s => s.ToString()));
                    this.logger.Debug($"DNS Gateway {this.options.DnsName} resolved to: \n {addressesStr}");
                }

                return endpointUris;
            }
            catch (System.Net.Sockets.SocketException se)
            {
                if (se.Message == "No such host is known")
                {
                    logger.Warn(123, $"No addresses found for silo gateways with DNS hostname: {this.options.DnsName}");
                    return new List<Uri>();
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
