using System;

namespace Orleans.Providers
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
}