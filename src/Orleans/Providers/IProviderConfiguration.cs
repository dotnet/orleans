using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.Providers
{
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

        void AddChildConfiguration(IProviderConfiguration config);
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
}