using System;
using System.Text.RegularExpressions;
using Orleans.GrainDirectory;
using Orleans.Streams;

namespace Orleans
{
    namespace MultiCluster
    {
        /// <summary>
        /// base class for multi cluster registration strategies.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public abstract class RegistrationAttribute : Attribute
        {
            internal MultiClusterRegistrationStrategy RegistrationStrategy { get; private set; }

            internal RegistrationAttribute(MultiClusterRegistrationStrategy strategy)
            {
                this.RegistrationStrategy = strategy;
            }
        }

        /// <summary>
        /// This attribute indicates that instances of the marked grain class will have a single instance across all available clusters. Any requests in any clusters will be forwarded to the single activation instance.
        /// </summary>
        public class GlobalSingleInstanceAttribute : RegistrationAttribute
        {
            public GlobalSingleInstanceAttribute()
                : base(GlobalSingleInstanceRegistration.Singleton)
            {
            }
        }

        /// <summary>
        /// This attribute indicates that instances of the marked grain class
        /// will have an independent instance for each cluster with
        /// no coordination.
        /// </summary>
        public class OneInstancePerClusterAttribute : RegistrationAttribute
        {
            public OneInstancePerClusterAttribute()
                : base(ClusterLocalRegistration.Singleton)
            {
            }
        }
    }

    namespace Providers
    {
        /// <summary>
        /// The [Orleans.Providers.StorageProvider] attribute is used to define which storage provider to use for persistence of grain state.
        /// <para>
        /// Specifying [Orleans.Providers.StorageProvider] property is recommended for all grains which extend Grain&lt;T&gt;.
        /// If no [Orleans.Providers.StorageProvider] attribute is  specified, then a "Default" strorage provider will be used.
        /// If a suitable storage provider cannot be located for this grain, then the grain will fail to load into the Silo.
        /// </para>
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class StorageProviderAttribute : Attribute
        {
            /// <summary>
            /// The name of the provider to be used for persisting of grain state
            /// </summary>
            public string ProviderName { get; set; }

            public StorageProviderAttribute()
            {
                ProviderName = Runtime.Constants.DEFAULT_STORAGE_PROVIDER_NAME;
            }
        }

        /// <summary>
        /// The [Orleans.Providers.LogConsistencyProvider] attribute is used to define which consistency provider to use for grains using the log-view state abstraction.
        /// <para>
        /// Specifying [Orleans.Providers.LogConsistencyProvider] property is recommended for all grains that derive
        /// from LogConsistentGrain, such as JournaledGrain.
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
}