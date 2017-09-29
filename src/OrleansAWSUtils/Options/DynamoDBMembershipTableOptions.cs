using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansAWSUtils.Configuration
{
    public class DynamoDBMembershipTableOptions
    {
        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Connection string for DynamoDB Storage
        /// </summary>
        public string DataConnectionString { get; set; }
    }
}
