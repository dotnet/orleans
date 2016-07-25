using System;
using System.Net;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using Xunit;
using System.Collections.Generic;
using Orleans.TestingHost.Utils;

namespace UnitTests.Deployment
{
    public class AzureSiloTests
    {
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async void ValidateConfiguration_Startup()
        {
            StorageEmulator.TryStart();

            var serviceRuntime = new TestServiceRuntimeWrapper();
            serviceRuntime.DeploymentId = "foo";
            serviceRuntime.Settings["DataConnectionString"] = "UseDevelopmentStorage=true";
            serviceRuntime.InstanceName = "name";

            var config = AzureSilo.DefaultConfiguration(serviceRuntime);
            config.AddMemoryStorageProvider();

            AzureSilo orleansAzureSilo = new AzureSilo(serviceRuntime);
            bool ok = await orleansAzureSilo.ValidateConfiguration(config);

            Assert.True(ok);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async void ValidateConfiguration_InvalidConnectionString()
        {
            var serviceRuntime = new TestServiceRuntimeWrapper();
            serviceRuntime.DeploymentId = "bar";
            serviceRuntime.Settings["DataConnectionString"] = "InvalidConnectionString";
            serviceRuntime.InstanceName = "name";

            var config = AzureSilo.DefaultConfiguration(serviceRuntime);
            config.AddMemoryStorageProvider();

            AzureSilo orleansAzureSilo = new AzureSilo(serviceRuntime);
            bool ok = await orleansAzureSilo.ValidateConfiguration(config);

            Assert.False(ok);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async void ValidateConfiguration_IncorrectKey()
        {
            var serviceRuntime = new TestServiceRuntimeWrapper();
            serviceRuntime.DeploymentId = "bar";
            serviceRuntime.Settings["DataConnectionString"] = "DefaultEndpointsProtocol=https;AccountName=orleanstest;AccountKey=IncorrectKey";
            serviceRuntime.InstanceName = "name";

            var config = AzureSilo.DefaultConfiguration(serviceRuntime);
            config.AddMemoryStorageProvider();

            AzureSilo orleansAzureSilo = new AzureSilo(serviceRuntime);
            bool ok = await orleansAzureSilo.ValidateConfiguration(config);

            Assert.False(ok);
        }
    }

    public class TestServiceRuntimeWrapper : IServiceRuntimeWrapper
    {
        public Dictionary<string, string> Settings = new Dictionary<string, string>();

        public string DeploymentId
        {
            get; set;
        }

        public int FaultDomain
        {
            get; set;
        }

        public string InstanceName
        {
            get; set;
        }

        public int RoleInstanceCount
        {
            get; set;
        }

        public string RoleName
        {
            get; set;
        }

        public int UpdateDomain
        {
            get; set;
        }

        public string GetConfigurationSettingValue(string configurationSettingName)
        {
            return Settings[configurationSettingName];
        }

        public IPEndPoint GetIPEndpoint(string endpointName)
        {
            return new IPEndPoint(1, 30000);
        }

        public void SubscribeForStoppingNotification(object handlerObject, EventHandler<object> handler)
        {
        }

        public void UnsubscribeFromStoppingNotification(object handlerObject, EventHandler<object> handler)
        {
        }
    }
}
