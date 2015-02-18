using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnitTestGrains;

namespace UnitTests.Streaming
{
    [DeploymentItem("Config_DevStorage.xml")]
    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class StreamProvidersTests_ProviderConfigNotLoaded : UnitTestBase
    {
        private static readonly FileInfo SiloConfig = new FileInfo("Config_DevStorage.xml");

        public static readonly string STREAM_PROVIDER_NAME = "SMSProvider";

        private static readonly Options siloOptions = new Options
        {
            SiloConfigFile = SiloConfig
        };

        public StreamProvidersTests_ProviderConfigNotLoaded()
            : base(siloOptions)
        {
            // loading the default config, without stream providers.
        }

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            ResetDefaultRuntimes();
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Providers")]
        public void ProvidersTests_ConfigNotLoaded()
        {
            bool hasThrown = false;
            Guid streamId = Guid.NewGuid();
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = Streaming_ConsumerGrainFactory.GetGrain(2, "UnitTestGrains.Streaming_ConsumerGrain");
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Config"), TestCategory("ServiceId"), TestCategory("Providers")]
        public void ServiceId_ProviderRuntime()
        {
            Guid thisRunServiceId = Globals.ServiceId;

            SiloHandle siloHandle = GetActiveSilos().First();
            Guid serviceId = siloHandle.Silo.GlobalConfig.ServiceId;
            Assert.AreEqual(thisRunServiceId, serviceId, "ServiceId in Silo config");
            serviceId = siloHandle.Silo.TestHookup.ServiceId;
            Assert.AreEqual(thisRunServiceId, serviceId, "ServiceId active in silo");

            // ServiceId is not currently available in client config
            //serviceId = ClientProviderRuntime.Instance.GetServiceId();
            //Assert.AreEqual(thisRunServiceId, serviceId, "ServiceId active in client");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Config"), TestCategory("ServiceId")]
        public void ServiceId_SiloRestart()
        {
            Guid configServiceId = Globals.ServiceId;

            var initialDeploymentId = DeploymentId;
            var initialServiceId = ServiceId;
            Console.WriteLine("DeploymentId={0} ServiceId={1}", DeploymentId, ServiceId);

            Assert.AreEqual(initialServiceId, configServiceId, "ServiceId in test config");

            Console.WriteLine("About to reset Silos .....");
            Console.WriteLine("Stopping Silos ...");
            ResetDefaultRuntimes();
            Console.WriteLine("Starting Silos ...");
            Initialize(siloOptions);
            Console.WriteLine("..... Silos restarted");

            Console.WriteLine("DeploymentId={0} ServiceId={1}", DeploymentId, ServiceId);

            Assert.AreEqual(initialServiceId, ServiceId, "ServiceId same after restart.");
            Assert.AreNotEqual(initialDeploymentId, DeploymentId, "DeploymentId different after restart.");

            SiloHandle siloHandle = GetActiveSilos().First();
            Guid serviceId = siloHandle.Silo.GlobalConfig.ServiceId;
            Assert.AreEqual(initialServiceId, serviceId, "ServiceId in Silo config");
            serviceId = siloHandle.Silo.TestHookup.ServiceId;
            Assert.AreEqual(initialServiceId, serviceId, "ServiceId active in silo");

            // ServiceId is not currently available in client config
            //serviceId = ClientProviderRuntime.Instance.GetServiceId();
            //Assert.AreEqual(initialServiceId, serviceId, "ServiceId active in client");
        }
    }

    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class StreamProvidersTests_ProviderConfigLoaded : UnitTestBase
    {
        public StreamProvidersTests_ProviderConfigLoaded()
            : base(new Options {
                SiloConfigFile = new FileInfo("Config_StreamProviders.xml")
            })
        {
        }

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            ResetDefaultRuntimes();
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Providers")]
        public void ProvidersTests_ProviderWrongName()
        {
            bool hasThrown = false;
            Guid streamId = Guid.NewGuid();
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = Streaming_ConsumerGrainFactory.GetGrain(2, "UnitTestGrains.Streaming_ConsumerGrain");
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