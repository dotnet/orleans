using System.Collections.Generic;

using Microsoft.Extensions.Options;

using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for <see cref="AzureBasedReminderTable"/>.
    /// </summary>
    public class AzureTableReminderStorageOptions
    {
        /// <summary>
        /// Gets or sets the storage connection string.
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }
    }
}