using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;

namespace Orleans.AzureUtils
{
    internal class AzureGatewayListProvider : IGatewayListProvider
    {
        private OrleansSiloInstanceManager siloInstanceManager;
        private ClientConfiguration config;
        private readonly ILoggerFactory loggerFactory;
        public AzureGatewayListProvider(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        #region Implementation of IGatewayListProvider

        public async Task InitializeGatewayListProvider(ClientConfiguration conf)
        {
            config = conf;
            siloInstanceManager = await OrleansSiloInstanceManager.GetManager(conf.DeploymentId, conf.DataConnectionString, this.loggerFactory);
        }
        // no caching
        public Task<IList<Uri>> GetGateways()
        {
            // FindAllGatewayProxyEndpoints already returns a deep copied List<Uri>.
            return siloInstanceManager.FindAllGatewayProxyEndpoints();
        }

        public TimeSpan MaxStaleness 
        {
            get { return config.GatewayListRefreshPeriod; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        #endregion
    }
}
