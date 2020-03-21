using System;

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
        /// Default preferred gateway index,. Value -1 means prefer no gateway
        /// </summary>
        public const int DEFAULT_PREFERED_GATEWAY_INDEX = -1;

        /// <summary>
        /// Preferred gateway index
        /// </summary>
        public int PreferedGatewayIndex { get; set; } = DEFAULT_PREFERED_GATEWAY_INDEX;
    }
}
