
using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Providers;

namespace OrleansServiceBusUtils.Providers.Streams.EventHub
{
    [Serializable]
    public class EventHubSettings : IEventHubSettings
    {
        private const string ConnectionStringName = "EventHubConnectionString";
        private const string ConsumerGroupName = "EventHubConsumerGroup";
        private const string PathName = "EventHubPath";
        private const string PrefetchCountName = "EventHubPrefetchCount";
        private const int InvalidPrefetchCount = -1;

        public EventHubSettings() { }

        public EventHubSettings(string connectionString, string consumerGroup, string path, int? prefetchCount = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException("connectionString");
            }
            if (string.IsNullOrWhiteSpace(consumerGroup))
            {
                throw new ArgumentNullException("consumerGroup");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentNullException("path");
            }
            ConnectionString = connectionString;
            ConsumerGroup = consumerGroup;
            Path = path;
            PrefetchCount = prefetchCount;
        }
        public string ConnectionString { get; private set; }
        public string ConsumerGroup { get; private set; }
        public string Path { get; private set; }
        public int? PrefetchCount { get; private set; }

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
                throw new ArgumentOutOfRangeException("providerConfiguration", ConnectionStringName + " not set.");
            }
            ConsumerGroup = providerConfiguration.GetProperty(ConsumerGroupName, null);
            if (string.IsNullOrWhiteSpace(ConsumerGroup))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", ConsumerGroupName + " not set.");
            }
            Path = providerConfiguration.GetProperty(PathName, null);
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", PathName + " not set.");
            }
            PrefetchCount = providerConfiguration.GetIntProperty(PrefetchCountName, InvalidPrefetchCount);
            if (PrefetchCount == InvalidPrefetchCount)
            {
                PrefetchCount = null;
            }
        }
    }
}
