using System;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;
using Orleans.Logging;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

#pragma warning disable CS0618 // Type or member is obsolete
namespace UnitTests
{
    public class ClientInitTests : OrleansTestingBase, IClassFixture<ClientInitTests.Fixture>
    {
        private readonly Fixture fixture;


        public class Fixture : BaseTestClusterFixture
        {
            public ClientConfiguration ClientConfiguration { get; private set; }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy => this.ClientConfiguration = legacy.ClientConfiguration);
            }
        }

        public ClientInitTests(Fixture fixture)
        {
            this.fixture = fixture;
            if (!GrainClient.IsInitialized)
            {
                GrainClient.ConfigureClientDelegate = null;
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

        [Fact, TestCategory("Functional"), TestCategory("Client")]
        public void ClientInit_InitializeWithDelegate()
        {
            var wasCalled = false;
            GrainClient.ConfigureClientDelegate = clientBuilder => wasCalled = true;
            GrainClient.Initialize(this.fixture.ClientConfiguration);
            Assert.True(wasCalled);
        }
    }
}

#pragma warning restore CS0618 // Type or member is obsolete