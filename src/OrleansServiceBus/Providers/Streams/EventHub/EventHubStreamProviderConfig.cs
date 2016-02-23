
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

        public const string CheckpointSettingsTypeName = "CheckpointSettingsType";
        public Type CheckpointSettingsType { get; set; }

        public string StreamProviderName { get; private set; }

        public const string CacheSizeMbName = "CacheSizeMb";
        private const int DefaultCacheSizeMb = 128; // default to 128mb cache.
        public int CacheSizeMb { get; private set; }

        public EventHubStreamProviderConfig(string streamProviderName, int cacheSizeMb = DefaultCacheSizeMb)
        {
            StreamProviderName = streamProviderName;
            CacheSizeMb = cacheSizeMb;
        }

        public void WriteProperties(Dictionary<string, string> properties)
        {
            if (EventHubSettingsType != null)
                properties.Add(EventHubConfigTypeName, EventHubSettingsType.AssemblyQualifiedName);
            if (CheckpointSettingsType != null)
                properties.Add(CheckpointSettingsTypeName, CheckpointSettingsType.AssemblyQualifiedName);
            properties.Add(CacheSizeMbName, CacheSizeMb.ToString(CultureInfo.InvariantCulture));
        }

        public void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventHubSettingsType = providerConfiguration.GetTypeProperty(EventHubConfigTypeName, null);
            CheckpointSettingsType = providerConfiguration.GetTypeProperty(CheckpointSettingsTypeName, null);
            if (string.IsNullOrWhiteSpace(StreamProviderName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "StreamProviderName not set.");
            }
            CacheSizeMb = providerConfiguration.GetIntProperty(CacheSizeMbName, DefaultCacheSizeMb);
        }

        public IEventHubSettings GetEventHubSettings(IProviderConfiguration providerConfig, IServiceProvider serviceProvider)
        {
            // if no event hub settings type is provided, use EventHubSettings and get populate settings from providerConfig
            if (EventHubSettingsType == null)
            {
                EventHubSettingsType = typeof(EventHubSettings);
            }

            var hubSettings = serviceProvider.GetService(EventHubSettingsType) as IEventHubSettings;
            if (hubSettings == null)
            {
                throw new ArgumentOutOfRangeException("providerConfig", "EventHubSettingsType not valid.");
            }

            // if settings is an EventHubSettings class, populate settings from providerConfig
            var settings = hubSettings as EventHubSettings;
            if (settings != null)
            {
                settings.PopulateFromProviderConfig(providerConfig);
            }

            return hubSettings;
        }

        public ICheckpointSettings GetCheckpointSettings(IProviderConfiguration providerConfig, IServiceProvider serviceProvider)
        {
            // if no checkpoint settings type is provided, use EventHubCheckpointSettings and get populate settings from providerConfig
            if (CheckpointSettingsType == null)
            {
                CheckpointSettingsType = typeof(EventHubCheckpointSettings);
            }

            var checkpointConfig = serviceProvider.GetService(CheckpointSettingsType) as ICheckpointSettings;
            if (checkpointConfig == null)
            {
                throw new ArgumentOutOfRangeException("providerConfig", "CheckpointSettingsType not valid.");
            }

            // if settings is an EventHubCheckpointSettings class, populate settings from providerConfig
            var settings = checkpointConfig as EventHubCheckpointSettings;
            if (settings != null)
            {
                settings.PopulateFromProviderConfig(providerConfig);
            }

            return checkpointConfig;
        }
    }
}
