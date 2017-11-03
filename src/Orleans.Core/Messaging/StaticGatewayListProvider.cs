using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    public class StaticGatewayListProvider : IGatewayListProvider
    {
        private readonly StaticGatewayListProviderOptions options;
        private readonly TimeSpan maxStaleness;
        public StaticGatewayListProvider(IOptions<StaticGatewayListProviderOptions> options, ClientConfiguration clientConfiguration )
        {
            this.options = options.Value;
            this.maxStaleness = clientConfiguration.GatewayListRefreshPeriod;
        }

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider() => Task.CompletedTask;
        

        public Task<IList<Uri>> GetGateways() => Task.FromResult(this.options.Gateways);

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
