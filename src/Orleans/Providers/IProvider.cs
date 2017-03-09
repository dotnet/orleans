using System.Threading.Tasks;

namespace Orleans.Providers
{
    /// <summary>
    /// Base interface for all type-specific provider interfaces in Orleans
    /// </summary>
    /// <seealso cref="Orleans.Providers.IBootstrapProvider"/>
    /// <seealso cref="Orleans.Storage.IStorageProvider"/>
    /// <seealso cref="Orleans.LogConsistency.ILogConsistencyProvider"/>

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
}