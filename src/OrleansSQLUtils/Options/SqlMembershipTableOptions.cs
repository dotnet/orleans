using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansSQLUtils.Configuration
{
    public class SqlMembershipTableOptions
    {
        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Connection string for Sql Storage
        /// </summary>
        public string DataConnectionString { get; set; }
        /// <summary>
        /// The invariant name of the connector for membership's database.
        /// </summary>
        public string AdoInvariant { get; set; }
    }
}
