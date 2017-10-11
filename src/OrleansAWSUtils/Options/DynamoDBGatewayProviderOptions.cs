using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansAWSUtils.Options
{
    public class DynamoDBGatewayProviderOptions
    {
        /// <summary>
        /// Connection string for DynamoDB Storage
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gateway refresh period
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; }
    }
}
