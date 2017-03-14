using System;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
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
                GrainClient.Initialize(fixture.HostedCluster.ClientConfiguration);
            }
        }

        protected TestCluster HostedCluster { get; set; }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_IsInitialized()
        {
            // First initialize will have been done by the default cluster fixture

            Assert.True(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_Uninitialize()
        {
            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_UnThenReinitialize()
        {
            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized);

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_MultiInitialize()
        {
            // First initialize will have been done by orleans unit test base class

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_ErrorDuringInitialize()
        {
            ClientConfiguration cfg = TestClusterOptions.BuildClientConfiguration(HostedCluster.ClusterConfiguration);
            cfg.TraceFileName = "TestOnlyThrowExceptionDuringInit.log";

            // First initialize will have been done by orleans unit test base class, so uninitialize back to null state
            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized, "GrainClient.IsInitialized");
            Assert.False(LogManager.IsInitialized, "Logger.IsInitialized");

            try
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = true;
                Assert.Throws<InvalidOperationException>(() =>
                    GrainClient.Initialize(cfg));

                Assert.False(GrainClient.IsInitialized, "GrainClient.IsInitialized");
                Assert.False(LogManager.IsInitialized, "Logger.IsInitialized");

                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;

                GrainClient.Initialize(cfg);
                Assert.True(GrainClient.IsInitialized, "GrainClient.IsInitialized");
                Assert.True(LogManager.IsInitialized, "Logger.IsInitialized");
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
            Assert.True(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized);

            GrainClient.Initialize(HostedCluster.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized);
        }
    }
}
