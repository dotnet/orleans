using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;

namespace Orleans.SqlUtils
{
    /// It turns out that <see cref="DbProviderFactories.GetFactory(string)"/> uses reflection to fetch the singleton instance on every call.
    /// this class caches the references to all loaded factories
    internal static class DbConnectionFactory
    {
        private static readonly ConcurrentDictionary<string, CachedFactory> factoryCache =
            new ConcurrentDictionary<string, CachedFactory>();

        private static CachedFactory GetFactory(string invariantName)
        {
            if (String.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentNullException(nameof(invariantName));
            }

            // Seeks for database provider factory classes from GAC or as indicated by
            // the configuration file, see at <see href="https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx">Obtaining a DbProviderFactory</see>.       

            DataRow factoryData = DbProviderFactories.GetFactoryClasses().Rows.Find(invariantName);

            if (factoryData == null)
            {
                throw new InvalidOperationException($"Can't find database provider factory with '{invariantName}' invariant name. Please check the application configuration file to see if the provider is configured correctly or the configured ADO.NET provider libraries are deployed with the application.");
            }

            var factoryName = factoryData["Name"].ToString();
            var description = factoryData["Description"].ToString();
            var assemblyQualifiedNameKey = factoryData["AssemblyQualifiedName"].ToString();
            var factory = DbProviderFactories.GetFactory(invariantName);
            return new CachedFactory(factory, factoryName, description, assemblyQualifiedNameKey);
        }

        public static DbConnection CreateConnection(string invariantName, string connectionString)
        {
            if (String.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentNullException(nameof(invariantName));
            }

            if (String.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            var factory = factoryCache.GetOrAdd(invariantName, GetFactory).Factory;
            var connection = factory.CreateConnection();

            if (connection == null)
            {
                throw new InvalidOperationException($"Database provider factory: '{invariantName}' did not return a connection object.");
            }

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
