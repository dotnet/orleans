using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansAzureUtils.Options
{
    public class AzureTableGatewayProviderOptions
    {
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gateway refresh period
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; }
    }
}
