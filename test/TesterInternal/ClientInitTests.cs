using System;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;
using Orleans.Logging;

#pragma warning disable CS0618 // Type or member is obsolete
namespace UnitTests
{
    public class ClientInitTests : OrleansTestingBase, IClassFixture<DefaultClusterFixture>
    {
        private readonly DefaultClusterFixture fixture;

        public ClientInitTests(DefaultClusterFixture fixture)
        {
            this.fixture = fixture;
            if (!GrainClient.IsInitialized)
            {
                GrainClient.Initialize(fixture.ClientConfiguration);
            }
        }

        protected TestCluster HostedCluster => this.fixture.HostedCluster;

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

            GrainClient.Initialize(fixture.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_MultiInitialize()
        {
            // First initialize will have been done by orleans unit test base class

            GrainClient.Initialize(this.fixture.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);

            GrainClient.Initialize(this.fixture.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_ErrorDuringInitialize()
        {
            ClientConfiguration cfg = this.fixture.ClientConfiguration;

            // First initialize will have been done by orleans unit test base class, so uninitialize back to null state
            GrainClient.Uninitialize();
            GrainClient.ConfigureLoggingDelegate = builder => builder.AddFile("TestOnlyThrowExceptionDuringInit.log");
            Assert.False(GrainClient.IsInitialized, "GrainClient.IsInitialized");

            try
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = true;
                Assert.Throws<InvalidOperationException>(() =>
                    GrainClient.Initialize(cfg));

                Assert.False(GrainClient.IsInitialized, "GrainClient.IsInitialized");

                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;

                GrainClient.Initialize(cfg);
                Assert.True(GrainClient.IsInitialized, "GrainClient.IsInitialized");
            }
            finally
            {
                OutsideRuntimeClient.TestOnlyThrowExceptionDuringInit = false;
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_InitializeUnThenReInit()
        {
            GrainClient.Initialize(this.fixture.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized);

            GrainClient.Initialize(this.fixture.ClientConfiguration);
            Assert.True(GrainClient.IsInitialized);

            GrainClient.Uninitialize();
            Assert.False(GrainClient.IsInitialized);
        }
    }
}

#pragma warning restore CS0618 // Type or member is obsolete