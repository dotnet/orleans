using System;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using UnitTests.Tester;
using Xunit;

namespace UnitTests
{
    public class ClientInitTests : OrleansTestingBase, IClassFixture<DefaultClusterFixture>
    {
        public ClientInitTests(DefaultClusterFixture fixture)
        {
            this.HostedCluster = fixture.HostedCluster;
            if (!GrainClient.IsInitialized)
            {
                this.HostedCluster.InitializeClient();
            }
        }

        protected TestCluster HostedCluster { get; set; }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_IsInitialized()
        {
            // First initialize will have been done by the default cluster fixture

            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_Uninitialize()
        {
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_UnThenReinitialize()
        {
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_MultiInitialize()
        {
            // First initialize will have been done by orleans unit test base class

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.IsTrue(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_ErrorDuringInitialize()
        {
            ClientConfiguration cfg = TestClusterOptions.BuildClientConfiguration(HostedCluster.ClusterConfiguration);
            cfg.TraceFileName = "TestOnlyThrowExceptionDuringInit.log";

            // First initialize will have been done by orleans unit test base class, so uninitialize back to null state
            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized, "GrainClient.IsInitialized");
            Assert.IsFalse(TraceLogger.IsInitialized, "Logger.IsInitialized");

            try
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = true;
                Xunit.Assert.Throws<InvalidOperationException>(() =>
                    GrainClient.Initialize(cfg));

                Assert.IsFalse(GrainClient.IsInitialized, "GrainClient.IsInitialized");
                Assert.IsFalse(TraceLogger.IsInitialized, "Logger.IsInitialized");

                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;

                GrainClient.Initialize(cfg);
                Assert.IsTrue(GrainClient.IsInitialized, "GrainClient.IsInitialized");
                Assert.IsTrue(TraceLogger.IsInitialized, "Logger.IsInitialized");
            }
            finally
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_InitializeUnThenReInit()
        {
            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.IsTrue(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.IsFalse(GrainClient.IsInitialized);
        }
    }
}
