
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class SMSClientStreamTests : TestClusterPerTest
    {
        private const string SMSStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public SMSClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddClientBuilderConfigurator<ClientConfiguretor>();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddSimpleMessageStreamProvider(SMSStreamProviderName)
                     .AddMemoryGrainStorage("PubSubStore")
                     .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
            }
        }
        public class ClientConfiguretor : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddSimpleMessageStreamProvider(SMSStreamProviderName);
            }
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ SMSStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SMSStreamProviderName, StreamNamespace);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ SMSStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(SMSStreamProviderName, StreamNamespace, output);
        }
    }
}
