using System.Reflection;


namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to OrleansProviders.dll 
    /// </summary>
    public static class ConfigExtensions
    {
        /// <summary>
        /// Adds default storage provider named "MemoryStore" of type Orleans.Storage.MemoryStorage
        /// </summary>
        /// <param name="config">Cluster configuration object to add provider to</param>
        /// <param name="providerName">Provider name</param>
        /// <returns></returns>
        public static void AddMemoryStorageProvider(this ClusterConfiguration config, string providerName = "MemoryStore")
        {
            config.Globals.RegisterStorageProvider<Storage.MemoryStorage>(providerName);
        }
    }
}