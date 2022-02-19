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
        private readonly TimeSpan maxStaleness;

        /// <summary>
        /// Initializes a new instance of the <see cref="StaticGatewayListProvider"/> class.
        /// </summary>
        /// <param name="options">The specific options.</param>
        /// <param name="gatewayOptions">The general gateway options.</param>
        public StaticGatewayListProvider(IOptions<StaticGatewayListProviderOptions> options, IOptions<GatewayOptions> gatewayOptions)
        {
            this.options = options.Value;
            this.maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        /// <inheritdoc />
        public Task InitializeGatewayListProvider() => Task.CompletedTask;
        
        /// <inheritdoc />
        public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(this.options.Gateways);

        /// <inheritdoc />
        public TimeSpan MaxStaleness
        {
            get => this.maxStaleness;
        }

        /// <inheritdoc />
        public bool IsUpdatable
        {
            get => true;
        }
    }
}
