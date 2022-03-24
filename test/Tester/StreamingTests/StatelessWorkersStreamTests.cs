using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests
{
    [TestCategory("Streaming")]
    public class StatelessWorkersStreamTests : OrleansTestingBase, IClassFixture<StatelessWorkersStreamTests.Fixture>
    {
        private readonly Fixture fixture;

        private readonly ILogger logger;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfiguretor>();
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

        private const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        public StatelessWorkersStreamTests(Fixture fixture)
        {
            this.fixture = fixture;
            logger = this.fixture.Logger;
        }

        [Fact, TestCategory("Functional")]
        public async Task SubscribeToStream_FromStatelessWorker_Fail()
        {
            this.logger.Info($"************************ { nameof(SubscribeToStream_FromStatelessWorker_Fail) } *********************************");
            var runner = new StatelessWorkersStreamTestsRunner(StreamProvider, this.logger, this.fixture.HostedCluster);
            await Assert.ThrowsAsync<InvalidOperationException>( () => runner.BecomeConsumer(Guid.NewGuid()));
        }

        [Fact, TestCategory("Functional")]
        public async Task ProduceToStream_FromStatelessWorker_Fail()
        {
            this.logger.Info($"************************ { nameof(SubscribeToStream_FromStatelessWorker_Fail) } *********************************");
            var runner = new StatelessWorkersStreamTestsRunner(StreamProvider, this.logger, this.fixture.HostedCluster);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ProduceMessage(Guid.NewGuid()));
        }
    }

    public class StatelessWorkersStreamTestsRunner
    {
        private const string StreamNamespace = "SampleStreamNamespace";

        private readonly string streamProvider;
        private readonly ILogger logger;
        private readonly TestCluster cluster;

        public StatelessWorkersStreamTestsRunner(string streamProvider, ILogger logger, TestCluster cluster)
        {
            this.streamProvider = streamProvider;
            this.logger = logger;
            this.cluster = cluster;
        }

        public async Task BecomeConsumer(Guid streamId)
        {
            var consumer = this.cluster.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(0);
            await consumer.BecomeConsumer(streamId, streamProvider);
        }

        public async Task ProduceMessage(Guid streamId)
        {
            var producer = this.cluster.GrainFactory.GetGrain<IStatelessWorkerStreamProducerGrain>(0);
            await producer.Produce(streamId, streamProvider, string.Empty);
        }
    }
}