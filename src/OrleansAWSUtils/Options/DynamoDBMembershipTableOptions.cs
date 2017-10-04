using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansAWSUtils.Configuration
{
    public class DynamoDBMembershipTableOptions
    {
        /// <summary>
        /// Connection string for DynamoDB Storage
        /// </summary>
        public string ConnectionString { get; set; }
    }
}
