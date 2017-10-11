using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansZooKeeperUtils.Options
{
    public class ZooKeeperGatewayProviderOptions
    {
        /// <summary>
        /// Connection string for ZooKeeper storage
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gateway refresh period
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; }
    }
}
