using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.ConsulUtils.Configuration
{
    /// <summary>
    /// Options for configuring ConsulBasedMembership
    /// </summary>
    public class ConsulMembershipOptions
    {
        /// <summary>
        /// Address for consul client
        /// </summary>
        public Uri Address { get; set; }
    }
}
