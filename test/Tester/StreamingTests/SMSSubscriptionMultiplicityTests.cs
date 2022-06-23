using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
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
        private readonly SubscriptionMultiplicityTestRunner _runner;
        private readonly Fixture _fixture;

        public SMSSubscriptionMultiplicityTests(Fixture fixture)
        {
            _fixture = fixture;
            _runner = new SubscriptionMultiplicityTestRunner(Fixture.StreamProvider, fixture.HostedCluster);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSMultipleSubscriptionTest()
        {
            _fixture.Logger.LogInformation("************************ SMSMultipleSubscriptionTest *********************************");
            await _runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSAddAndRemoveSubscriptionTest()
        {
            _fixture.Logger.LogInformation("************************ SMSAddAndRemoveSubscriptionTest *********************************");
            await _runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSResubscriptionTest()
        {
            _fixture.Logger.LogInformation("************************ SMSResubscriptionTest *********************************");
            await _runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSResubscriptionAfterDeactivationTest()
        {
            _fixture.Logger.LogInformation("************************ ResubscriptionAfterDeactivationTest *********************************");
            await _runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSActiveSubscriptionTest()
        {
            _fixture.Logger.LogInformation("************************ SMSActiveSubscriptionTest *********************************");
            await _runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task SMSSubscribeFromClientTest()
        {
            _fixture.Logger.LogInformation("************************ SMSSubscribeFromClientTest *********************************");
            await _runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
