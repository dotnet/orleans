using System;
using System.Collections.Generic;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to OrleansAzureUtils.dll 
    /// </summary>
    public static class AzureConfigurationExtensions
    {
        /// <summary>
        /// Adds a storage provider of type <see cref="Orleans.Storage.AzureTableStorage"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name</param>
        /// <param name="connectionString">The azure storage connection string.</param>
        /// <param name="tableName">The table name where to store the state.</param>
        /// <param name="deleteOnClear">Whether the provider deletes the state when <see cref="IStorageProvider.ClearStateAsync"/> is called.</param>
        /// <param name="useJsonFormat">Whether is stores the content as JSON or as binary in Azure Table.</param>
        /// <param name="useFullAssemblyNames">Whether to use full assembly names in the serialized JSON. This value is ignored if <paramref name="useJsonFormat"/> is false.</param>
        /// <param name="indentJson">Whether to indent (pretty print) the JSON. This value is ignored if <paramref name="useJsonFormat"/> is false.</param>
        public static void AddAzureTableStorageProvider(
            this ClusterConfiguration config,
            string providerName = "AzureTableStore",
            string connectionString = null,
            string tableName = AzureTableStorage.TableNamePropertyDefaultValue,
            bool deleteOnClear = false,
            bool useJsonFormat = false,
            bool useFullAssemblyNames = false,
            bool indentJson = false)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

            var properties = new Dictionary<string, string>
            {
                { AzureTableStorage.DataConnectionStringPropertyName, connectionString },
                { AzureTableStorage.TableNamePropertyName, tableName },
                { AzureTableStorage.DeleteOnClearPropertyName, deleteOnClear.ToString() },
                { AzureTableStorage.UseJsonFormatPropertyName, useJsonFormat.ToString() },
            };

            if (useJsonFormat)
            {
                properties.Add(SerializationManager.UseFullAssemblyNamesProperty, useFullAssemblyNames.ToString());
                properties.Add(SerializationManager.IndentJsonProperty, indentJson.ToString());
            }

            config.Globals.RegisterStorageProvider<AzureTableStorage>(providerName, properties);
        }

        /// <summary>
        /// Adds a storage provider of type <see cref="Orleans.Storage.AzureBlobStorage"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name</param>
        /// <param name="connectionString">The azure storage connection string.</param>
        /// <param name="containerName">The container name where to store the state.</param>
        /// <param name="useFullAssemblyNames">Whether to use full assembly names in the serialized JSON.</param>
        /// <param name="indentJson">Whether to indent (pretty print) the JSON.</param>
        public static void AddAzureBlobStorageProvider(
            this ClusterConfiguration config,
            string providerName = "AzureBlobStore",
            string connectionString = null,
            string containerName = AzureBlobStorage.ContainerNamePropertyDefaultValue,
            bool useFullAssemblyNames = false,
            bool indentJson = false)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

            var properties = new Dictionary<string, string>
            {
                { AzureBlobStorage.DataConnectionStringPropertyName, connectionString },
                { AzureBlobStorage.ContainerNamePropertyName, containerName },
                { SerializationManager.UseFullAssemblyNamesProperty, useFullAssemblyNames.ToString() },
                { SerializationManager.IndentJsonProperty, indentJson.ToString() },
            };

            config.Globals.RegisterStorageProvider<AzureBlobStorage>(providerName, properties);
        }
    }
}