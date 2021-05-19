using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SMSDeactivationTests : TestClusterPerTest
    {
        private const string SMSStreamProviderName = "SMSProvider";
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private DeactivationTestRunner runner;
        public static readonly TimeSpan CollectionAge = GrainCollectionOptions.DEFAULT_COLLECTION_QUANTUM + TimeSpan.FromSeconds(1);

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new DeactivationTestRunner(SMSStreamProviderName, this.Client);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<ClientConfiguretor>();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME)
                     .AddMemoryGrainStorage("PubSubStore")
                     .Configure<GrainCollectionOptions>(op =>
                    {
                        op.CollectionAge = CollectionAge;
                        op.ClassSpecificCollectionAge.Add(typeof(MultipleSubscriptionConsumerGrain).FullName, TimeSpan.FromHours(2));
                    })
                    .Configure<SiloMessagingOptions>(op=>op.ResponseTimeout = TimeSpan.FromMinutes(30));
            }
        }
        public class ClientConfiguretor : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest_ClientConsumer()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest_ClientConsumer(Guid.NewGuid(), StreamNamespace);
        }

    }
}
