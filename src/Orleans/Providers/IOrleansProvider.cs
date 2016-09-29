using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Providers
{
#pragma warning disable 1574
    /// <summary>
    /// Base interface for all type-specific provider interfaces in Orleans
    /// </summary>
    /// <seealso cref="Orleans.Providers.IBootstrapProvider"/>
    /// <seealso cref="Orleans.Storage.IStorageProvider"/>
    public interface IProvider
    {
        /// <summary>The name of this provider instance, as given to it in the config.</summary>
        string Name { get; }

        /// <summary>
        /// Initialization function called by Orleans Provider Manager when a new provider class instance  is created
        /// </summary>
        /// <param name="name">Name assigned for this provider</param>
        /// <param name="providerRuntime">Callback for accessing system functions in the Provider Runtime</param>
        /// <param name="config">Configuration metadata to be used for this provider instance</param>
        /// <returns>Completion promise Task for the inttialization work for this provider</returns>
        Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config);

        /// <summary>Close function for this provider instance.</summary>
        /// <returns>Completion promise for the Close operation on this provider.</returns>
        Task Close();
    }
    #pragma warning restore 1574

    /// <summary>
    /// Internal provider management interface for instantiating dependent providers in a hierarchical tree of dependencies
    /// </summary>
    public interface IProviderManager
    {
        /// <summary>
        /// Call into Provider Manager for instantiating dependent providers in a hierarchical tree of dependencies
        /// </summary>
        /// <param name="name">Name of the provider to be found</param>
        /// <returns>Provider instance with the given name</returns>
        IProvider GetProvider(string name);
    }

    /// <summary>
    /// Configuration information that a provider receives
    /// </summary>
    public interface IProviderConfiguration
    {
        /// <summary>
        /// Full type name of this provider.
        /// </summary>
        string Type { get; }

        /// <summary>
        /// Name of this provider.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Configuration properties for this provider instance, as name-value pairs.
        /// </summary>
        ReadOnlyDictionary<string, string> Properties { get; }

        /// <summary>
        /// Nested providers in case of a hierarchical tree of dependencies
        /// </summary>
        IList<IProvider> Children { get; }

        /// <summary>
        /// Set a property in this provider configuration.
        /// If the property with this key already exists, it is been overwritten with the new value, otherwise it is just added.
        /// </summary>
        /// <param name="key">The key of the property</param>
        /// <param name="val">The value of the property</param>
        /// <returns>Provider instance with the given name</returns>
        void SetProperty(string key, string val);

        /// <summary>
        /// Removes a property in this provider configuration.
        /// </summary>
        /// <param name="key">The key of the property.</param>
        /// <returns>True if the property was found and removed, false otherwise.</returns>
        bool RemoveProperty(string key);

    }

    public static class ProviderConfigurationExtensions
    {
        public static int GetIntProperty(this IProviderConfiguration config, string key, int settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? int.Parse(s) : settingDefault;
        }

        public static string GetProperty(this IProviderConfiguration config, string key, string settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? s : settingDefault;
        }

        public static Guid GetGuidProperty(this IProviderConfiguration config, string key, Guid settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? Guid.Parse(s) : settingDefault;
        }

        public static T GetEnumProperty<T>(this IProviderConfiguration config, string key, T settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? (T)Enum.Parse(typeof(T),s) : settingDefault;
        }

        public static Type GetTypeProperty(this IProviderConfiguration config, string key, Type settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? Type.GetType(s) : settingDefault;
        }

        public static bool GetBoolProperty(this IProviderConfiguration config, string key, bool settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? bool.Parse(s) : settingDefault;
        }

        public static TimeSpan GetTimeSpanProperty(this IProviderConfiguration config, string key, TimeSpan settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? TimeSpan.Parse(s) : settingDefault;
        }
    }

    /// <summary>
    /// Exception thrown whenever a provider has failed to be initialized.
    /// </summary>
    [Serializable]
    public class ProviderInitializationException : OrleansException
    {
        public ProviderInitializationException()
        { }
        public ProviderInitializationException(string msg)
            : base(msg)
        { }
        public ProviderInitializationException(string msg, Exception exc)
            : base(msg, exc)
        { }
#if !NETSTANDARD
        protected ProviderInitializationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }
}
