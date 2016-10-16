using Orleans.Storage;
using System;
using System.Collections.Generic;


namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// The format in which the data will serialized to the storage.
    /// </summary>
    public enum AdoNetSerializationFormat
    {
        /// <summary>
        /// The native Orleans binary serialization format.
        /// </summary>
        OrleansNativeBinary = 1,

        /// <summary>
        /// XML.
        /// </summary>
        Xml                 = 2,

        /// <summary>
        /// JSON.
        /// </summary>
        Json                = 3
    }


    /// <summary>
    /// Extension methods for configuration classes specific to <see cref="OrleansSQLUtils"/>.
    /// </summary>
    public static class AdoNetConfigurationExtensions
    {
        /// <summary>
        /// Adds a storage provider of type <see cref="AdoNetStorageProvider"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="connectionString">The ADO.NET storage connection string. If none is provided, it will use the same as in the Globals configuration.</param>
        /// <param name="serializationFormat">The format in which the data will serialized to the storage.</param>
        public static void AddAdoNetStorageProvider(
            this ClusterConfiguration config,
            string providerName = "AdoNetStorage",
            string connectionString = null,
            AdoNetSerializationFormat serializationFormat = AdoNetSerializationFormat.Xml)
        {
            if(string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentNullException(nameof(providerName));
            }

            connectionString = GetConnectionString(connectionString, config);
            var properties = new Dictionary<string, string> { { AdoNetStorageProvider.DataConnectionStringPropertyName, connectionString } };

            //In the following the call to ToString is documented to return a non-localized string
            //and internally just a literal so should be quick. See more at https://technet.microsoft.com/fi-fi/library/s802ct92.
            if(serializationFormat == AdoNetSerializationFormat.Json)
            {
                properties[AdoNetStorageProvider.UseJsonFormatPropertyName] = true.ToString();
            }
            else if(serializationFormat == AdoNetSerializationFormat.Xml)
            {
                properties[AdoNetStorageProvider.UseXmlFormatPropertyName] = true.ToString();
            }
            else
            {
                properties[AdoNetStorageProvider.UseBinaryFormatPropertyName] = true.ToString();
            }

            config.Globals.RegisterStorageProvider<AdoNetStorageProvider>(providerName, properties);
        }


        private static string GetConnectionString(string connectionString, ClusterConfiguration config)
        {
            if(!string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            if(!string.IsNullOrWhiteSpace(config.Globals.DataConnectionString))
            {
                return config.Globals.DataConnectionString;
            }

            throw new ArgumentNullException(nameof(connectionString), "Parameter value and fallback value are both null or empty.");
        }
    }
}
