using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring how clients interact with gateway endpoints.
    /// </summary>
    public class GatewayOptions
    {
        /// <summary>
        /// Gets or sets the period of time between refreshing the list of active gateways.
        /// </summary>
        /// <value>The list of active gateways will be refreshed every minute by default.</value>
        public TimeSpan GatewayListRefreshPeriod { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Default preferred gateway index,. Value -1 means prefer no gateway
        /// </summary>
        public const int DEFAULT_PREFERED_GATEWAY_INDEX = -1;

        /// <summary>
        /// Gets or sets the index of the preferred gateway within the list of active gateways.
        /// </summary>
        /// <remarks>Set this value to its default value, <c>-1</c>, to disable this functionality.</remarks>
        /// <value>No gateway is preferred by default.</value>
        public int PreferedGatewayIndex { get; set; } = DEFAULT_PREFERED_GATEWAY_INDEX;
    }
}
