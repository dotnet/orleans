using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    public class MonitoringStorageOptions
    {
        /// <summary>
        /// Data connection string for statistic table and metric table
        /// </summary>
        public string DataConnectionString { get; set; }
    }
}
