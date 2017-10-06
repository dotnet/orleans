using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration.Options
{
    /// <summary>
    /// Options for Configure ConfigBasedStaticGateway
    /// </summary>
    public class StaticGatewayProviderOptions
    {
        /// <summary>
        /// Static gateways to use
        /// </summary>
        public IList<Uri> Gateways { get; set; }

        /// <summary>
        /// Gateway refresh period
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; }
    }
}
