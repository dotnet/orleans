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

        /// <summary>
        /// Gets or sets a value indicating whether grain references wait for a compatible implementation to appear in the cluster manifest.
        /// </summary>
        /// <remarks>
        /// When disabled, requesting a grain interface which has no known implementation throws immediately.
        /// </remarks>
        public bool EnableDeferredGrainTypeResolution { get; set; } = true;

        /// <summary>
        /// The default interval between cluster grain interface map refreshes.
        /// </summary>
        public static readonly TimeSpan DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME = TimeSpan.FromMinutes(1);
    }
}
