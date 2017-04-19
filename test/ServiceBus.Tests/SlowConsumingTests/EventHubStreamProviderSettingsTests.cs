using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NonSilo.Tests.StreamingTests
{

    /// <summary>
    /// EventHubAdapterFactory populate EventHubStreamProviderSettings from IProviderConfiguration.
    /// So this test suit tests that EventHubStreamProviderSettings will be populated back as the same as before
    /// it is written into ProviderConfiguration. 
    /// </summary>
    public class EventHubStreamProviderSettingsTests
    {
        private static string StreamProviderName = "EHStreamProvider";
        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public void DefaultSetting_Write_Into_ProviderConfiguration_PopulateBack()
        {
            var expectedSetting = new EventHubStreamProviderSettings(StreamProviderName);
            AssertSettingEqual_After_WriteInto_ProviderConfiguration_AndPopulateBack(expectedSetting);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public void SettingWithSlowConsumingMonitorSetUp_Write_Into_ProviderConfiguration_PopulateBack()
        {
            var expectedSetting = new EventHubStreamProviderSettings(StreamProviderName);
            expectedSetting.SlowConsumingMonitorPressureWindowSize = TimeSpan.FromMinutes(2);
            expectedSetting.SlowConsumingMonitorFlowControlThreshold = 1 / 3;
            AssertSettingEqual_After_WriteInto_ProviderConfiguration_AndPopulateBack(expectedSetting);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
        public void SettingWithAvgConsumingMonitorSetUp_Write_Into_ProviderConfiguration_PopulateBack()
        {
            var expectedSetting = new EventHubStreamProviderSettings(StreamProviderName);
            expectedSetting.AveragingCachePressureMonitorFlowControlThreshold = 1 / 10;
            AssertSettingEqual_After_WriteInto_ProviderConfiguration_AndPopulateBack(expectedSetting);
        }

        private void AssertSettingEqual_After_WriteInto_ProviderConfiguration_AndPopulateBack(EventHubStreamProviderSettings expectedSetting)
        {
            var properties = new Dictionary<string, string>();
            expectedSetting.WriteProperties(properties);
            var config = new ProviderConfiguration(properties, typeof(EventHubStreamProvider).FullName, StreamProviderName);

            var actualSettings = new EventHubStreamProviderSettings(StreamProviderName);
            actualSettings.PopulateFromProviderConfig(config);
            AssertEqual(expectedSetting, actualSettings);
        }
        private void AssertEqual(EventHubStreamProviderSettings expectedSettings, EventHubStreamProviderSettings actualSettings)
        {
            Assert.Equal(expectedSettings.StreamProviderName, actualSettings.StreamProviderName);
            Assert.Equal(expectedSettings.SlowConsumingMonitorFlowControlThreshold, actualSettings.SlowConsumingMonitorFlowControlThreshold);
            Assert.Equal(expectedSettings.SlowConsumingMonitorPressureWindowSize, actualSettings.SlowConsumingMonitorPressureWindowSize);
            Assert.Equal(expectedSettings.AveragingCachePressureMonitorFlowControlThreshold, actualSettings.AveragingCachePressureMonitorFlowControlThreshold);
            Assert.Equal(expectedSettings.EventHubSettingsType, actualSettings.EventHubSettingsType);
            Assert.Equal(expectedSettings.CheckpointerSettingsType, actualSettings.CheckpointerSettingsType);
            Assert.Equal(expectedSettings.CacheSizeMb, actualSettings.CacheSizeMb);
            Assert.Equal(expectedSettings.DataMinTimeInCache, actualSettings.DataMinTimeInCache);
            Assert.Equal(expectedSettings.DataMaxAgeInCache, actualSettings.DataMaxAgeInCache);
        }
    }
}
