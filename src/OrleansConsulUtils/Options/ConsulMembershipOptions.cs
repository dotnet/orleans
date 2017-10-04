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
        /// Connection string for Consul Storage
        /// </summary>
        public string ConnectionString { get; set; }
    }
}
