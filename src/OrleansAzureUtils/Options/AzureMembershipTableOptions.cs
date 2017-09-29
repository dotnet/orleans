using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.AzureUtils.Configuration
{
    /// <summary>
    /// Specify options used for <see cref="AzureBasedMembershipTable"/>
    /// </summary>
    public class AzureMembershipTableOptions
    {
        /// <summary>
        /// Retry count for Azure Table operations. 
        /// </summary>
        public int MaxStorageBusyRetries { get; set; }
        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        public string DataConnectionString { get; set; }
    }

}
