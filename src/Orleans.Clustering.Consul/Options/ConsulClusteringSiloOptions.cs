using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring ConsulBasedMembership
    /// </summary>
    public class ConsulClusteringSiloOptions
    {
        /// <summary>
        /// Address for consul client
        /// </summary>
        public Uri Address { get; set; }
    }
}
