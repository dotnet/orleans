using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration.Options
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
