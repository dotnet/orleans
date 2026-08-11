using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Messaging
{
    /// <summary>
    /// <see cref="IGatewayListProvider"/> implementation which returns a static list, configured via <see cref="StaticGatewayListProviderOptions"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="StaticGatewayListProvider"/> class.
    /// </remarks>
    /// <param name="optionsAccessor">The specific options.</param>
    /// <param name="gatewayOptions">The general gateway options.</param>
    public class StaticGatewayListProvider(IOptions<StaticGatewayListProviderOptions> optionsAccessor, IOptions<GatewayOptions> gatewayOptions) : IGatewayListProvider
    {
        private readonly StaticGatewayListProviderOptions options = optionsAccessor.Value;
        private readonly TimeSpan maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;

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
