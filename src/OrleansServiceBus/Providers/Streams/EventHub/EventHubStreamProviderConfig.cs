
using System;
using System.Collections.Generic;
using Orleans.Providers;

namespace Orleans.ServiceBus.Providers
{
    public class EventHubStreamProviderConfig
    {
        public const string EventHubConfigTypeName = "EventHubSettingsType";
        public Type EventHubSettingsType { get; set; }

        public string StreamProviderName { get; private set; }

        public int CacheSize { get { return 1024; } }

        public EventHubStreamProviderConfig(string streamProviderName)
        {
            StreamProviderName = streamProviderName;
        }

        public void WriteProperties(Dictionary<string, string> properties)
        {
            if (EventHubSettingsType!=null)
                properties.Add(EventHubConfigTypeName, EventHubSettingsType.AssemblyQualifiedName);
        }

        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventHubSettingsType = providerConfiguration.GetTypeProperty(EventHubConfigTypeName, null);
            if (string.IsNullOrWhiteSpace(StreamProviderName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "StreamProviderName not set.");
            }
        }
    }
}
