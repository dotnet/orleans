
using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Settings class for EventHubStreamProvider.
    /// </summary>
    public class EventHubStreamProviderSettings : RecoverableStreamProviderSettings
    {
        /// <summary>
        /// Stream provider name.  This setting is required.
        /// </summary>
        public string StreamProviderName { get; }

        /// <summary>
        /// SlowConsumingMonitorFlowControlThresholdName
        /// </summary>
        public const string SlowConsumingMonitorFlowControlThresholdName = nameof(SlowConsumingMonitorFlowControlThreshold);

        /// <summary>
        /// SlowConsumingPressureMonitorConfig
        /// </summary>
        public double? SlowConsumingMonitorFlowControlThreshold { get; set; }

        /// <summary>
        /// SlowConsumingMonitorPressureWindowSizeName
        /// </summary>
        public const string SlowConsumingMonitorPressureWindowSizeName = nameof(SlowConsumingMonitorPressureWindowSize);

        /// <summary>
        /// SlowConsumingMonitorPressureWindowSize
        /// </summary>
        public TimeSpan? SlowConsumingMonitorPressureWindowSize { get; set; }

        /// <summary>
        /// AveragingCachePressureMonitorFlowControlThresholdName
        /// </summary>
        public const string AveragingCachePressureMonitorFlowControlThresholdName = nameof(AveragingCachePressureMonitorFlowControlThreshold);

        /// <summary>
        /// AveragingCachePressureMonitorFlowControlThreshold, AveragingCachePressureMonitor is turn on by default. 
        /// User can turn it off by setting this value to null
        /// </summary>
        public double? AveragingCachePressureMonitorFlowControlThreshold = AveragingCachePressureMonitor.DefaultThreshold;
        private const double CachePressureMonitorOffThreshold = 1.0;
        /// <summary>
        /// EventHubSettingsType setting name.
        /// </summary>
        public const string EventHubConfigTypeName = "EventHubSettingsType";
        /// <summary>
        /// EventHub configuration type.  Type must conform to IEventHubSettings interface.
        /// </summary>
        public Type EventHubSettingsType { get; set; }

        /// <summary>
        /// CheckpointerSettingsType setting name.
        /// </summary>
        public const string CheckpointerSettingsTypeName = "CheckpointerSettingsType";
        /// <summary>
        /// Checkpoint settings type.  Type must conform to ICheckpointerSettings interface.
        /// </summary>
        public Type CheckpointerSettingsType { get; set; }

        /// <summary>
        /// Constructor.  Requires provider name.
        /// </summary>
        /// <param name="streamProviderName"></param>
        public EventHubStreamProviderSettings(string streamProviderName)
        {
            StreamProviderName = streamProviderName;
        }

        /// <summary>
        /// Writes settings into a property bag.
        /// </summary>
        /// <param name="properties"></param>
        public override void WriteProperties(Dictionary<string, string> properties)
        {
            base.WriteProperties(properties);
            if (EventHubSettingsType != null)
                properties.Add(EventHubConfigTypeName, EventHubSettingsType.AssemblyQualifiedName);
            if (CheckpointerSettingsType != null)
                properties.Add(CheckpointerSettingsTypeName, CheckpointerSettingsType.AssemblyQualifiedName);
            if (AveragingCachePressureMonitorFlowControlThreshold.HasValue)
            {
                properties.Add(AveragingCachePressureMonitorFlowControlThresholdName, AveragingCachePressureMonitorFlowControlThreshold.ToString());
            }
            else
            {
                properties.Add(AveragingCachePressureMonitorFlowControlThresholdName, CachePressureMonitorOffThreshold.ToString());
            }
            if (SlowConsumingMonitorPressureWindowSize.HasValue)
            {
                properties.Add(SlowConsumingMonitorPressureWindowSizeName, SlowConsumingMonitorPressureWindowSize.ToString());
            }
            if (SlowConsumingMonitorFlowControlThreshold.HasValue)
            {
                properties.Add(SlowConsumingMonitorFlowControlThresholdName, SlowConsumingMonitorFlowControlThreshold.ToString());
            }
        }

        /// <summary>
        /// Read settings from provider configuration.
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public override void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventHubSettingsType = providerConfiguration.GetTypeProperty(EventHubConfigTypeName, null);
            CheckpointerSettingsType = providerConfiguration.GetTypeProperty(CheckpointerSettingsTypeName, null);
            if (string.IsNullOrWhiteSpace(StreamProviderName))
            {
                throw new ArgumentOutOfRangeException(nameof(providerConfiguration), "StreamProviderName not set.");
            }
            base.PopulateFromProviderConfig(providerConfiguration);
            double flowControlThreshold = 0;
            if (providerConfiguration.TryGetDoubleProperty(SlowConsumingMonitorFlowControlThresholdName, out flowControlThreshold))
            {
                this.SlowConsumingMonitorFlowControlThreshold = flowControlThreshold;
            }
            TimeSpan pressureWindowSize = TimeSpan.Zero;
            if (providerConfiguration.TryGetTimeSpanProperty(SlowConsumingMonitorPressureWindowSizeName, out pressureWindowSize))
            {
                this.SlowConsumingMonitorPressureWindowSize = pressureWindowSize;
            }
            if (providerConfiguration.TryGetDoubleProperty(AveragingCachePressureMonitorFlowControlThresholdName, out flowControlThreshold))
            {
                if (flowControlThreshold >= CachePressureMonitorOffThreshold)
                    this.AveragingCachePressureMonitorFlowControlThreshold = null;
                else
                    this.AveragingCachePressureMonitorFlowControlThreshold = flowControlThreshold;
            }
        }

        /// <summary>
        /// Aquire configured IEventHubSettings class
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
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
            settings?.PopulateFromProviderConfig(providerConfig);

            return hubSettings;
        }

        /// <summary>
        /// Aquire configured ICheckpointerSettings class
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
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
            settings?.PopulateFromProviderConfig(providerConfig);

            return checkpointerSettings;
        }
    }
}
