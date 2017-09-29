using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansZooKeeperUtils.Configuration
{
    public class ZooKeeperMembershipTableOptions
    {
        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Connection string for ZooKeeper Storage
        /// </summary>
        public string DataConnectionString { get; set; }
    }
}
