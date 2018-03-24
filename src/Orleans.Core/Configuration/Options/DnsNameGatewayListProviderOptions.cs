using System;
using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for Configure DnsNameGatewayListProviderOptions
    /// </summary>
    public class DnsNameGatewayListProviderOptions
    {
        /// <summary>
        /// Dns name to use
        /// </summary>
        public string DnsName { get; set; }

        /// <summary>
        /// Port to use
        /// </summary>
        public int Port { get; set; }
    }
}
