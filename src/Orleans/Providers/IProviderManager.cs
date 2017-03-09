namespace Orleans.Providers
{
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
}