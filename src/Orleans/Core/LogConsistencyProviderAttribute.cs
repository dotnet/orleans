using System;

namespace Orleans.Providers
{
    /// <summary>
    /// The [Orleans.Providers.LogConsistencyProvider] attribute is used to define which consistency provider to use for grains using the log-view state abstraction.
    /// <para>
    /// Specifying [Orleans.Providers.LogConsistencyProvider] property is recommended for all grains that derive
    /// from ILogConsistentGrain, such as JournaledGrain.
    /// If no [Orleans.Providers.LogConsistencyProvider] attribute is  specified, then the runtime tries to locate
    /// one as follows. First, it looks for a 
    /// "Default" provider in the configuration file, then it checks if the grain type defines a default.
    /// If a consistency provider cannot be located for this grain, then the grain will fail to load into the Silo.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class LogConsistencyProviderAttribute : Attribute
    {
        /// <summary>
        /// The name of the provider to be used for consistency
        /// </summary>
        public string ProviderName { get; set; }

        public LogConsistencyProviderAttribute()
        {
            ProviderName = Runtime.Constants.DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME;
        }
    }
}