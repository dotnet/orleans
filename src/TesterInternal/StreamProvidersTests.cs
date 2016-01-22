using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.Streaming
{
    [DeploymentItem("Config_DevStorage.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class StreamProvidersTests_ProviderConfigNotLoaded : HostedTestClusterPerFixture
    {
        private static readonly FileInfo SiloConfig = new FileInfo("Config_DevStorage.xml");

        public static readonly string STREAM_PROVIDER_NAME = "SMSProvider";

        private static readonly Guid initialServiceId = Guid.NewGuid();

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            SiloConfigFile = SiloConfig,
            AdjustConfig = config =>
            {
                config.Globals.ServiceId = initialServiceId;
            }
        };

        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(siloOptions);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public void ProvidersTests_ConfigNotLoaded()
        {
            bool hasThrown = false;
            Guid streamId = Guid.NewGuid();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
            try
            {
                consumer.BecomeConsumer(streamId, STREAM_PROVIDER_NAME, null).Wait();
            }
            catch(Exception exc)
            {
                hasThrown = true;
                Exception baseException = exc.GetBaseException();
                Assert.AreEqual(typeof(KeyNotFoundException), baseException.GetType());
            }
            Assert.IsTrue(hasThrown, "Should have thrown.");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId"), TestCategory("Providers")]
        public void ServiceId_ProviderRuntime()
        {
            Guid thisRunServiceId = this.HostedCluster.Globals.ServiceId;

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = siloHandle.Silo.GlobalConfig.ServiceId;
            Assert.AreEqual(thisRunServiceId, serviceId, "ServiceId in Silo config");
            serviceId = siloHandle.Silo.TestHook.ServiceId;
            Assert.AreEqual(thisRunServiceId, serviceId, "ServiceId active in silo");

            // ServiceId is not currently available in client config
            //serviceId = ClientProviderRuntime.Instance.GetServiceId();
            //Assert.AreEqual(thisRunServiceId, serviceId, "ServiceId active in client");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId")]
        public void ServiceId_SiloRestart()
        {
            Guid configServiceId = this.HostedCluster.Globals.ServiceId;

            var initialDeploymentId = this.HostedCluster.DeploymentId;
            Console.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, initialServiceId);

            Assert.AreEqual(initialServiceId, configServiceId, "ServiceId in test config");

            Console.WriteLine("About to reset Silos .....");
            Console.WriteLine("Stopping Silos ...");
            this.HostedCluster.StopDefaultSilos();
            Console.WriteLine("Starting Silos ...");
            this.HostedCluster.RedeployTestingSiloHost(siloOptions);
            Console.WriteLine("..... Silos restarted");

            Console.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, this.HostedCluster.Globals.ServiceId);

            Assert.AreEqual(initialServiceId, this.HostedCluster.Globals.ServiceId, "ServiceId same after restart.");
            Assert.AreNotEqual(initialDeploymentId, this.HostedCluster.DeploymentId, "DeploymentId different after restart.");

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = siloHandle.Silo.GlobalConfig.ServiceId;
            Assert.AreEqual(initialServiceId, serviceId, "ServiceId in Silo config");
            serviceId = siloHandle.Silo.TestHook.ServiceId;
            Assert.AreEqual(initialServiceId, serviceId, "ServiceId active in silo");

            // ServiceId is not currently available in client config
            //serviceId = ClientProviderRuntime.Instance.GetServiceId();
            //Assert.AreEqual(initialServiceId, serviceId, "ServiceId active in client");
        }
    }

    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class StreamProvidersTests_ProviderConfigLoaded : HostedTestClusterPerFixture
    {
        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(
                new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_StreamProviders.xml")
                });
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public void ProvidersTests_ProviderWrongName()
        {
            bool hasThrown = false;
            Guid streamId = Guid.NewGuid();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
            try
            {
                consumer.BecomeConsumer(streamId, "WrongProviderName", null).Wait();
            }
            catch (Exception exc)
            {
                Exception baseException = exc.GetBaseException();
                Assert.AreEqual(typeof(KeyNotFoundException), baseException.GetType());
            }
            hasThrown = true;
            Assert.IsTrue(hasThrown);
        }
    }
}