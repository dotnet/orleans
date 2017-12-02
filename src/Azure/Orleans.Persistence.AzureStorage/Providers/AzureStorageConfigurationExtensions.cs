using System;
using System.Collections.Generic;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to Orleans.Persistence.AzureStorage.dll 
    /// </summary>
    public static class AzureStorageConfigurationExtensions
    {
        /// <summary>
        /// Adds a storage provider of type <see cref="AzureTableStorage"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="connectionString">The azure storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="tableName">The table name where to store the state.</param>
        /// <param name="deleteOnClear">Whether the provider deletes the state when <see cref="IStorageProvider.ClearStateAsync"/> is called.</param>
        /// <param name="useJsonFormat">Whether is stores the content as JSON or as binary in Azure Table.</param>
        /// <param name="useFullAssemblyNames">Whether to use full assembly names in the serialized JSON. This value is ignored if <paramref name="useJsonFormat"/> is false.</param>
        /// <param name="indentJson">Whether to indent (pretty print) the JSON. This value is ignored if <paramref name="useJsonFormat"/> is false.</param>
        public static void AddAzureTableStorageProvider(
            this ClusterConfiguration config,
            string providerName = "AzureTableStore",
            string connectionString = null,
            string tableName = AzureTableStorage.TableNameDefaultValue,
            bool deleteOnClear = false,
            bool useJsonFormat = false,
            bool useFullAssemblyNames = false,
            bool indentJson = false)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));
            connectionString = GetConnectionString(connectionString, config);

            var properties = new Dictionary<string, string>
            {
                { AzureTableStorage.DataConnectionStringPropertyName, connectionString },
                { AzureTableStorage.TableNamePropertyName, tableName },
                { AzureTableStorage.DeleteOnClearPropertyName, deleteOnClear.ToString() },
                { AzureTableStorage.UseJsonFormatPropertyName, useJsonFormat.ToString() },
            };

            if (useJsonFormat)
            {
                properties.Add(OrleansJsonSerializer.UseFullAssemblyNamesProperty, useFullAssemblyNames.ToString());
                properties.Add(OrleansJsonSerializer.IndentJsonProperty, indentJson.ToString());
            }

            config.Globals.RegisterStorageProvider<AzureTableStorage>(providerName, properties);
        }

        /// <summary>
        /// Adds a storage provider of type <see cref="AzureBlobStorage"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="connectionString">The azure storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="containerName">The container name where to store the state.</param>
        /// <param name="useFullAssemblyNames">Whether to use full assembly names in the serialized JSON.</param>
        /// <param name="indentJson">Whether to indent (pretty print) the JSON.</param>
        public static void AddAzureBlobStorageProvider(
            this ClusterConfiguration config,
            string providerName = "AzureBlobStore",
            string connectionString = null,
            string containerName = AzureBlobStorage.ContainerNameDefaultValue,
            bool useFullAssemblyNames = false,
            bool indentJson = false)
        {
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));
            connectionString = GetConnectionString(connectionString, config);

            var properties = new Dictionary<string, string>
            {
                { AzureBlobStorage.DataConnectionStringPropertyName, connectionString },
                { AzureBlobStorage.ContainerNamePropertyName, containerName },
                { OrleansJsonSerializer.UseFullAssemblyNamesProperty, useFullAssemblyNames.ToString() },
                { OrleansJsonSerializer.IndentJsonProperty, indentJson.ToString() },
            };

            config.Globals.RegisterStorageProvider<AzureBlobStorage>(providerName, properties);
        }

        private static string GetConnectionString(string connectionString, ClusterConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(connectionString)) return connectionString;
            if (!string.IsNullOrWhiteSpace(config.Globals.DataConnectionString)) return config.Globals.DataConnectionString;

            throw new ArgumentNullException(nameof(connectionString),
                "Parameter value and fallback value are both null or empty.");
        }

        private static string GetConnectionString(string connectionString, ClientConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(connectionString)) return connectionString;
            if (!string.IsNullOrWhiteSpace(config.DataConnectionString)) return config.DataConnectionString;

            throw new ArgumentNullException(nameof(connectionString),
                "Parameter value and fallback value are both null or empty.");
        }
    }
}