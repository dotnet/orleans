
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

        public const string CheckpointerSettingsTypeName = "CheckpointerSettingsType";
        public Type CheckpointerSettingsType { get; set; }

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
            if (CheckpointerSettingsType != null)
                properties.Add(CheckpointerSettingsTypeName, CheckpointerSettingsType.AssemblyQualifiedName);
            properties.Add(CacheSizeMbName, CacheSizeMb.ToString(CultureInfo.InvariantCulture));
        }

        public void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventHubSettingsType = providerConfiguration.GetTypeProperty(EventHubConfigTypeName, null);
            CheckpointerSettingsType = providerConfiguration.GetTypeProperty(CheckpointerSettingsTypeName, null);
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

            var hubSettings = (IEventHubSettings)(serviceProvider?.GetService(EventHubSettingsType) ?? Activator.CreateInstance(EventHubSettingsType));
            if (hubSettings == null)
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfig), "EventHubSettingsType not valid.");
            }

            // if settings is an EventHubSettings class, populate settings from providerConfig
            var settings = hubSettings as EventHubSettings;
            if (settings != null)
            {
                settings.PopulateFromProviderConfig(providerConfig);
            }

            return hubSettings;
        }

        public ICheckpointerSettings GetCheckpointerSettings(IProviderConfiguration providerConfig, IServiceProvider serviceProvider)
        {
            // if no checkpointer settings type is provided, use EventHubCheckpointerSettings and get populate settings from providerConfig
            if (CheckpointerSettingsType == null)
            {
                CheckpointerSettingsType = typeof(EventHubCheckpointerSettings);
            }

            var checkpointerSettings = (ICheckpointerSettings)(serviceProvider?.GetService(CheckpointerSettingsType) ?? Activator.CreateInstance(CheckpointerSettingsType));
            if (checkpointerSettings == null)
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfig), "CheckpointerSettingsType not valid.");
            }

            // if settings is an EventHubCheckpointerSettings class, populate settings from providerConfig
            var settings = checkpointerSettings as EventHubCheckpointerSettings;
            if (settings != null)
            {
                settings.PopulateFromProviderConfig(providerConfig);
            }

            return checkpointerSettings;
        }
    }
}
