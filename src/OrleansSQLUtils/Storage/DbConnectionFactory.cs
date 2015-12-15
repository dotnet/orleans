using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace Orleans.SqlUtils
{
    /// It turns out that <see cref="DbProviderFactories.GetFactory(string)"/> uses reflection to fetch the singleton instance on every call.
    /// this class caches the references to all loaded factories
    internal static class DbConnectionFactory
    {
        private static readonly Dictionary<string, CachedFactory> factoryCache =
            new Dictionary<string, CachedFactory>();

        static DbConnectionFactory()
        {
            // Seeks for database provider factory classes from GAC or as indicated by
            // the configuration file, see at <see href="https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx">Obtaining a DbProviderFactory</see>.       
            
            foreach (DataRow factoryData in DbProviderFactories.GetFactoryClasses().Rows)
            {
                var invariantName = factoryData["InvariantName"].ToString();
                var factoryName = factoryData["Name"].ToString();
                var description = factoryData["Description"].ToString();
                var assemblyQualifiedNameKey = factoryData["AssemblyQualifiedName"].ToString();
                var factory = DbProviderFactories.GetFactory(invariantName);
                var cachedFactory = new CachedFactory(factory, factoryName, description, assemblyQualifiedNameKey);
                factoryCache.Add(invariantName, cachedFactory);
            }
        }

        public static DbConnection CreateConnection(string invariantName, string connectionString)
        {
            var factory = factoryCache[invariantName].Factory;
            var connection = factory.CreateConnection();
            connection.ConnectionString = connectionString;
            return connection;
        }
        
        private class CachedFactory
        {
            public CachedFactory(DbProviderFactory factory, string factoryName, string factoryDescription, string factoryAssemblyQualifiedNameKey)
            {
                Factory = factory;
                FactoryName = factoryName;
                FactoryDescription = factoryDescription;
                FactoryAssemblyQualifiedNameKey = factoryAssemblyQualifiedNameKey;
            }

            /// <summary>
            /// The factory to provide vendor specific functionality.
            /// </summary>
            /// <remarks>For more about <see href="http://florianreischl.blogspot.fi/2011/08/adonet-connection-pooling-internals-and.html">ConnectionPool</see>
            /// and issues with using this factory. Take these notes into account when considering robustness of Orleans!</remarks>
            public readonly DbProviderFactory Factory;

            /// <summary>
            /// The name of the loaded factory, set by a database connector vendor.
            /// </summary>
            public readonly string FactoryName;

            /// <summary>
            /// The description of the loaded factory, set by a database connector vendor.
            /// </summary>
            public readonly string FactoryDescription;

            /// <summary>
            /// The description of the loaded factory, set by a database connector vendor.
            /// </summary>
            public readonly string FactoryAssemblyQualifiedNameKey;
        }
    }
}
