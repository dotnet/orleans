
using System;
using System.Collections.Generic;
using Orleans.Providers;

namespace Orleans.ServiceBus.Providers
{
    public interface ICheckpointerSettings
    {
        /// <summary>
        /// Azure table storage data connections string
        /// </summary>
        string DataConnectionString { get; }

        /// <summary>
        /// Azure storage table name where the checkpoints will be stored
        /// </summary>
        string TableName { get; }

        /// <summary>
        /// How often to persist the checkpoints, if they've changed.
        /// </summary>
        TimeSpan PersistInterval { get; }

        /// <summary>
        /// This name partitions a service's checkpoint information from other services.
        /// </summary>
        string CheckpointNamespace { get; }
    }

    public class EventHubCheckpointerSettings : ICheckpointerSettings
    {
        private const string DataConnectionStringName = "CheckpointerDataConnectionString";
        private const string TableNameName = "CheckpointTableName";
        private const string PersistIntervalName = "CheckpointPersistInterval";
        private const string CheckpointNamespaceName = "CheckpointNamespace";
        private static readonly TimeSpan DefaultPersistInterval = TimeSpan.FromMinutes(1);

        public EventHubCheckpointerSettings(){}

        public EventHubCheckpointerSettings(string dataConnectionString, string table, string checkpointNamespace, TimeSpan? persistInterval = null)
        {
            if (string.IsNullOrWhiteSpace(dataConnectionString))
            {
                throw new ArgumentNullException("dataConnectionString");
            }
            if (string.IsNullOrWhiteSpace(table))
            {
                throw new ArgumentNullException("table");
            }
            if (string.IsNullOrWhiteSpace(checkpointNamespace))
            {
                throw new ArgumentNullException("checkpointNamespace");
            }
            DataConnectionString = dataConnectionString;
            TableName = table;
            CheckpointNamespace = checkpointNamespace;
            PersistInterval = persistInterval.HasValue ? persistInterval.Value : DefaultPersistInterval;
        }

        public string DataConnectionString { get; private set; }
        public string TableName { get; private set; }
        public TimeSpan PersistInterval { get; private set; }
        public string CheckpointNamespace { get; private set; }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(DataConnectionStringName, DataConnectionString);
            properties.Add(TableNameName, TableName);
            properties.Add(PersistIntervalName, PersistInterval.ToString());
            properties.Add(CheckpointNamespaceName, CheckpointNamespace);
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            DataConnectionString = providerConfiguration.GetProperty(DataConnectionStringName, null);
            if (string.IsNullOrWhiteSpace(DataConnectionString))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", DataConnectionStringName + " not set.");
            }
            TableName = providerConfiguration.GetProperty(TableNameName, null);
            if (string.IsNullOrWhiteSpace(TableName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", TableNameName + " not set.");
            }
            PersistInterval = providerConfiguration.GetTimeSpanProperty(PersistIntervalName, DefaultPersistInterval);
            CheckpointNamespace = providerConfiguration.GetProperty(CheckpointNamespaceName, null);
            if (string.IsNullOrWhiteSpace(CheckpointNamespace))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", CheckpointNamespaceName + " not set.");
            }
        }
    }
}
