using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    public class GatewayOptions
    {
        /// <summary>
        /// Default gateway list refresh period
        /// </summary>
        public static readonly TimeSpan DEFAULT_GATEWAY_LIST_REFRESH_PERIOD = TimeSpan.FromMinutes(1);
        /// <summary>
        /// Gateway list refresh period
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; } = DEFAULT_GATEWAY_LIST_REFRESH_PERIOD;
        /// <summary>
        /// Prefered gateway index
        /// </summary>
        public int PreferedGatewayIndex { get; set; } = -1;
    }
}
