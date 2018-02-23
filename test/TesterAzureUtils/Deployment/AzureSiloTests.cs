using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Deployment
{
    public class AzureSiloTests : IDisposable
    {
        private ILoggerFactory loggerFactory;
        public AzureSiloTests()
        {
            this.loggerFactory = TestingUtils.CreateDefaultLoggerFactory("AzureSiloTests.log");
        }

        public void Dispose()
        {
            this.loggerFactory.Dispose();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task ValidateConfiguration_Startup()
        {
            TestUtils.CheckForAzureStorage();

            await ValidateConfigurationAtStartup(TestDefaultConfiguration.DataConnectionString);
        }

        [SkippableFact]
        public async Task ValidateConfiguration_Startup_Emulator()
        {
            Skip.IfNot(StorageEmulator.TryStart(), "This test explicitly requires the Azure Storage emulator to run");

            await ValidateConfigurationAtStartup("UseDevelopmentStorage=true");
        }

        private async Task ValidateConfigurationAtStartup(string connectionString)
        {
            var serviceRuntime = new TestServiceRuntimeWrapper
            {
                DeploymentId = "foo"
            };
            serviceRuntime.Settings["DataConnectionString"] = connectionString;
            serviceRuntime.InstanceName = "name";

            var config = AzureSilo.DefaultConfiguration(serviceRuntime);

            AzureSilo orleansAzureSilo = new AzureSilo(serviceRuntime, this.loggerFactory);
            bool ok = await orleansAzureSilo.ValidateConfiguration(config);

            Assert.True(ok);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ValidateConfiguration_InvalidConnectionString()
        {
            var serviceRuntime = new TestServiceRuntimeWrapper
            {
                DeploymentId = "bar"
            };
            serviceRuntime.Settings["DataConnectionString"] = "InvalidConnectionString";
            serviceRuntime.InstanceName = "name";

            var config = AzureSilo.DefaultConfiguration(serviceRuntime);

            AzureSilo orleansAzureSilo = new AzureSilo(serviceRuntime, this.loggerFactory);
            bool ok = await orleansAzureSilo.ValidateConfiguration(config);

            Assert.False(ok);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ValidateConfiguration_IncorrectKey()
        {
            var serviceRuntime = new TestServiceRuntimeWrapper
            {
                DeploymentId = "bar"
            };
            serviceRuntime.Settings["DataConnectionString"] = "DefaultEndpointsProtocol=https;AccountName=orleanstest;AccountKey=IncorrectKey";
            serviceRuntime.InstanceName = "name";

            var config = AzureSilo.DefaultConfiguration(serviceRuntime);

            AzureSilo orleansAzureSilo = new AzureSilo(serviceRuntime, this.loggerFactory);
            bool ok = await orleansAzureSilo.ValidateConfiguration(config);

            Assert.False(ok);
        }
    }

    public class TestServiceRuntimeWrapper : IServiceRuntimeWrapper
    {
        public Dictionary<string, string> Settings = new Dictionary<string, string>();

        public string DeploymentId { get; set; }
        public int FaultDomain { get; set; }
        public string InstanceName { get; set; }
        public int RoleInstanceCount { get; set; }
        public string RoleName { get; set; }
        public int UpdateDomain { get; set; }

        public string GetConfigurationSettingValue(string configurationSettingName)
        {
            return this.Settings[configurationSettingName];
        }

        public IPEndPoint GetIPEndpoint(string endpointName)
        {
            return new IPEndPoint(1, 30000);
        }

        public void SubscribeForStoppingNotification(object handlerObject, EventHandler<object> handler) { }

        public void UnsubscribeFromStoppingNotification(object handlerObject, EventHandler<object> handler) { }
    }
}
