
using System;
using System.Collections.Generic;
using Orleans.Providers;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Setting interface for checkpointer
    /// </summary>
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

    /// <summary>
    /// EventHub checkpointer.
    /// </summary>
    public class EventHubCheckpointerSettings : ICheckpointerSettings
    {
        private const string DataConnectionStringName = "CheckpointerDataConnectionString";
        private const string TableNameName = "CheckpointTableName";
        private const string PersistIntervalName = "CheckpointPersistInterval";
        private const string CheckpointNamespaceName = "CheckpointNamespace";
        private static readonly TimeSpan DefaultPersistInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Default constructor/
        /// </summary>
        public EventHubCheckpointerSettings(){}

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dataConnectionString">Azure table storage connections string.</param>
        /// <param name="table">table name.</param>
        /// <param name="checkpointNamespace">checkpointer namespace.</param>
        /// <param name="persistInterval">checkpoint interval.</param>
        public EventHubCheckpointerSettings(string dataConnectionString, string table, string checkpointNamespace, TimeSpan? persistInterval = null)
        {
            if (string.IsNullOrWhiteSpace(dataConnectionString))
            {
                throw new ArgumentNullException(nameof(dataConnectionString));
            }
            if (string.IsNullOrWhiteSpace(table))
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (string.IsNullOrWhiteSpace(checkpointNamespace))
            {
                throw new ArgumentNullException(nameof(checkpointNamespace));
            }
            DataConnectionString = dataConnectionString;
            TableName = table;
            CheckpointNamespace = checkpointNamespace;
            PersistInterval = persistInterval ?? DefaultPersistInterval;
        }

        /// <summary>
        /// Azure table storage connections string.
        /// </summary>
        public string DataConnectionString { get; private set; }
        /// <summary>
        /// Azure table name.
        /// </summary>
        public string TableName { get; private set; }
        /// <summary>
        /// Intervale to write checkpoints.  Prevents spamming storage.
        /// </summary>
        public TimeSpan PersistInterval { get; private set; }
        /// <summary>
        /// Unique namespace for checkpoint data.  Is similar to consumer group.
        /// </summary>
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
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), DataConnectionStringName + " not set.");
            }
            TableName = providerConfiguration.GetProperty(TableNameName, null);
            if (string.IsNullOrWhiteSpace(TableName))
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), TableNameName + " not set.");
            }
            PersistInterval = providerConfiguration.GetTimeSpanProperty(PersistIntervalName, DefaultPersistInterval);
            CheckpointNamespace = providerConfiguration.GetProperty(CheckpointNamespaceName, null);
            if (string.IsNullOrWhiteSpace(CheckpointNamespace))
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), CheckpointNamespaceName + " not set.");
            }
        }
    }
}
