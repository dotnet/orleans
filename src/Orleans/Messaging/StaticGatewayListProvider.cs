using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    internal class StaticGatewayListProvider : IGatewayListProvider
    {
        private IList<Uri> knownGateways;
        private ClientConfiguration config;

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider(ClientConfiguration cfg, Logger logger)
        {
            config = cfg;
            knownGateways = cfg.Gateways.Select(ep => Utils.ToGatewayUri((IPEndPoint) ep)).ToList();
            return TaskDone.Done;
        }

        public Task<IList<Uri>> GetGateways()
        {
            return Task.FromResult(knownGateways);
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