using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Type management settings for in place upgrade.
    /// </summary>
    public class TypeManagementOptions
    {
        /// <summary>
        /// The number of seconds to refresh the cluster grain interface map
        /// </summary>
        public TimeSpan TypeMapRefreshInterval { get; set; } = DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME;
        public static readonly TimeSpan DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME = TimeSpan.FromMinutes(1);
    }
}
