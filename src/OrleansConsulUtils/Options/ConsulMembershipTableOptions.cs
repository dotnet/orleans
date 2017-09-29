using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.ConsulUtils.Configuration
{
    /// <summary>
    /// Options for configuring ConsulBasedMembershipTable
    /// </summary>
    public class ConsulMembershipTableOptions
    {
        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Connection string for Consul Storage
        /// </summary>
        public string DataConnectionString { get; set; }
    }
}
