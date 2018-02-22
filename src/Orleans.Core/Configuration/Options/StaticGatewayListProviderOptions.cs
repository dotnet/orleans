using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

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
        public IList<Uri> Gateways { get; set; } = new List<Uri>();
    }
}
