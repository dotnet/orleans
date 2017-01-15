
using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Providers;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// EventHub settings for a specific hub
    /// </summary>
    [Serializable]
    public class EventHubSettings : IEventHubSettings
    {
        private const string ConnectionStringName = "EventHubConnectionString";
        private const string ConsumerGroupName = "EventHubConsumerGroup";
        private const string PathName = "EventHubPath";
        private const string PrefetchCountName = "EventHubPrefetchCount";
        private const int InvalidPrefetchCount = -1;
        private const string StartFromNowName = "StartFromNow";
        private const bool StartFromNowDefault = true;

        /// <summary>
        /// Default constructor
        /// </summary>
        public EventHubSettings(){}

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString">EventHub connection string.</param>
        /// <param name="consumerGroup">EventHub consumer group.</param>
        /// <param name="path">Hub path.</param>
        /// <param name="startFromNow">In cases where no checkpoint is found, this indicates if service should read from the most recent data, or from the begining of a partition.</param>
        /// <param name="prefetchCount">optional parameter that configures the receiver prefetch count.</param>
        public EventHubSettings(string connectionString, string consumerGroup, string path, bool startFromNow = StartFromNowDefault, int? prefetchCount = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }
            if (string.IsNullOrWhiteSpace(consumerGroup))
            {
                throw new ArgumentNullException(nameof(consumerGroup));
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentNullException(nameof(path));
            }
            ConnectionString = connectionString;
            ConsumerGroup = consumerGroup;
            Path = path;
            PrefetchCount = prefetchCount;
            StartFromNow = startFromNow;
        }

        /// <summary>
        /// EventHub connection string.
        /// </summary>
        public string ConnectionString { get; private set; }
        /// <summary>
        /// EventHub consumer group.
        /// </summary>
        public string ConsumerGroup { get; private set; }
        /// <summary>
        /// Hub path.
        /// </summary>
        public string Path { get; private set; }
        /// <summary>
        /// Optional parameter that configures the receiver prefetch count.
        /// </summary>
        public int? PrefetchCount { get; private set; }
        /// <summary>
        /// In cases where no checkpoint is found, this indicates if service should read from the most recent data, or from the begining of a partition.
        /// </summary>
        public bool StartFromNow { get; private set; }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(ConnectionStringName, ConnectionString);
            properties.Add(ConsumerGroupName, ConsumerGroup);
            properties.Add(PathName, Path);
            if (PrefetchCount != null)
            {
                properties.Add(PrefetchCountName, PrefetchCount.Value.ToString(CultureInfo.InvariantCulture));
            }
            properties.Add(StartFromNowName, StartFromNow.ToString());
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            ConnectionString = providerConfiguration.GetProperty(ConnectionStringName, null);
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), ConnectionStringName + " not set.");
            }
            ConsumerGroup = providerConfiguration.GetProperty(ConsumerGroupName, null);
            if (string.IsNullOrWhiteSpace(ConsumerGroup))
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), ConsumerGroupName + " not set.");
            }
            Path = providerConfiguration.GetProperty(PathName, null);
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), PathName + " not set.");
            }
            PrefetchCount = providerConfiguration.GetIntProperty(PrefetchCountName, InvalidPrefetchCount);
            if (PrefetchCount == InvalidPrefetchCount)
            {
                PrefetchCount = null;
            }
            StartFromNow = providerConfiguration.GetBoolProperty(StartFromNowName, StartFromNowDefault);
        }
    }
}
