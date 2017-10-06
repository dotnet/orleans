using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Options;

namespace Orleans.Messaging
{
    public class StaticGatewayListProvider : IGatewayListProvider
    {
        private readonly StaticGatewayProviderOptions options;

        public StaticGatewayListProvider(IOptions<StaticGatewayProviderOptions> options)
        {
            this.options = options.Value;
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
            get { return options.GatewayListRefreshPeriod; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        #endregion
    }
}
