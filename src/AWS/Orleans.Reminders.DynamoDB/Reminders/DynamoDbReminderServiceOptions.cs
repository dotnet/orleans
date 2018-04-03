using System.Collections.Generic;

using Microsoft.Extensions.Options;

using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }
    }
}