
using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Providers;

namespace Orleans.ServiceBus.Providers
{
    public class EventHubStreamProviderConfig
    {
        public const string EventHubConfigTypeName = "EventHubSettingsType";
        public Type EventHubSettingsType { get; set; }

        public string StreamProviderName { get; private set; }

        public const string CacheSizeMbName = "CacheSizeMb";
        private const int DefaultCacheSizeMb = 100; // default to 100mb cache.
        public int CacheSizeMb { get; private set; }

        public EventHubStreamProviderConfig(string streamProviderName)
        {
            StreamProviderName = streamProviderName;
            CacheSizeMb = DefaultCacheSizeMb;
        }

        public void WriteProperties(Dictionary<string, string> properties)
        {
            if (EventHubSettingsType!=null)
                properties.Add(EventHubConfigTypeName, EventHubSettingsType.AssemblyQualifiedName);
            properties.Add(CacheSizeMbName, CacheSizeMb.ToString(CultureInfo.InvariantCulture));
        }

        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventHubSettingsType = providerConfiguration.GetTypeProperty(EventHubConfigTypeName, null);
            if (string.IsNullOrWhiteSpace(StreamProviderName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "StreamProviderName not set.");
            }
            CacheSizeMb = providerConfiguration.GetIntProperty(CacheSizeMbName, DefaultCacheSizeMb);
        }
    }
}
