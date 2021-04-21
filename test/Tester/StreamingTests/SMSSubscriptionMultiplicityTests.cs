using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SMSSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<SMSSubscriptionMultiplicityTests.Fixture>
    {
       
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddClientBuilderConfigurator<ClientConfiguretor>();
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddSimpleMessageStreamProvider(StreamProvider)
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }
            public class ClientConfiguretor : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddSimpleMessageStreamProvider(StreamProvider);
                }
            }
        }

        private const string StreamNamespace = "SMSSubscriptionMultiplicityTestsNamespace";
        private readonly SubscriptionMultiplicityTestRunner runner;
        private readonly Fixture fixture;

        public SMSSubscriptionMultiplicityTests(Fixture fixture)
        {
            this.fixture = fixture;
            runner = new SubscriptionMultiplicityTestRunner(Fixture.StreamProvider, fixture.HostedCluster);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSMultipleSubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ SMSMultipleSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSAddAndRemoveSubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ SMSAddAndRemoveSubscriptionTest *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSResubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ SMSResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSResubscriptionAfterDeactivationTest()
        {
            this.fixture.Logger.LogInformation("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSActiveSubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ SMSActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSSubscribeFromClientTest()
        {
            this.fixture.Logger.LogInformation("************************ SMSSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
