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
        private readonly StaticGatewayProviderOptions options;
        private readonly TimeSpan maxStaleness;
        public StaticGatewayListProvider(IOptions<StaticGatewayProviderOptions> options, ClientConfiguration clientConfiguration )
        {
            this.options = options.Value;
            this.maxStaleness = clientConfiguration.GatewayListRefreshPeriod;
        }

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider()
        {
            return Task.CompletedTask;
        }

        public Task<IList<Uri>> GetGateways()
        {
            return Task.FromResult(this.options.Gateways);
        }

        public TimeSpan MaxStaleness 
        {
            get { return this.maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        #endregion
    }
}
