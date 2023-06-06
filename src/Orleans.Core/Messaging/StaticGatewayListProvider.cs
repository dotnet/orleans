using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Messaging
{
    /// <summary>
    /// <see cref="IGatewayListProvider"/> implmementation which returns a static list, configured via <see cref="StaticGatewayListProviderOptions"/>.
    /// </summary>
    public class StaticGatewayListProvider : IGatewayListProvider
    {
        private readonly StaticGatewayListProviderOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="StaticGatewayListProvider"/> class.
        /// </summary>
        /// <param name="options">The specific options.</param>
        /// <param name="gatewayOptions">The general gateway options.</param>
        public StaticGatewayListProvider(IOptions<StaticGatewayListProviderOptions> options, IOptions<GatewayOptions> gatewayOptions)
        {
            this.options = options.Value;
            MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        /// <inheritdoc />
        public Task InitializeGatewayListProvider() => Task.CompletedTask;
        
        /// <inheritdoc />
        public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(options.Gateways);

        /// <inheritdoc />
        public TimeSpan MaxStaleness { get; }

        /// <inheritdoc />
        public bool IsUpdatable => true;
    }
}
