using System;
using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for Configure StaticGatewayListProvider
    /// </summary>
    public class StaticGatewayListProviderOptions
    {
        /// <summary>
        /// Static gateways to use
        /// </summary>
        public List<Uri> Gateways { get; set; } = new List<Uri>();
    }
}
