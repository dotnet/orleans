
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class MemoryStreamProviderClientTests : OrleansTestingBase, IClassFixture<MemoryStreamProviderClientTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = nameof(MemoryStreamProvider);
            public const string StreamNamespace = "StreamNamespace";

            public static readonly SimpleGeneratorConfig GeneratorConfig = new SimpleGeneratorConfig
            {
                StreamNamespace = StreamNamespace,
                EventsInStream = 100
            };

            public static readonly MemoryAdapterConfig AdapterConfig = new MemoryAdapterConfig(StreamProviderName);

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    AdjustConfig(legacy.ClusterConfiguration);
                    AdjustConfig(legacy.ClientConfiguration);
                });
            }

            private static void AdjustConfig(ClusterConfiguration config)
            {
                // register stream provider
                config.AddMemoryStorageProvider("PubSubStore");
                config.Globals.RegisterStreamProvider<MemoryStreamProvider>(StreamProviderName, BuildProviderSettings());
                config.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
            }

            private static void AdjustConfig(ClientConfiguration config)
            {
                config.RegisterStreamProvider<MemoryStreamProvider>(StreamProviderName, BuildProviderSettings());
            }

            private static Dictionary<string, string> BuildProviderSettings()
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderConfig.WriteProperties(settings);
                return settings;
            }
        }

        private static readonly MemoryAdapterConfig ProviderConfig = new MemoryAdapterConfig(Fixture.StreamProviderName);
        private readonly ITestOutputHelper output = null;
        private readonly ClientStreamTestRunner runner;

        private Fixture fixture;

        public MemoryStreamProviderClientTests(Fixture fixture)
        {
            this.fixture = fixture;
            runner = new ClientStreamTestRunner(fixture.HostedCluster);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MemoryStreamProducerOnDroppedClientTest()
        {
            this.fixture.Logger.Info("************************ MemoryStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(Fixture.StreamProviderName, Fixture.StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MemoryStreamConsumerOnDroppedClientTest()
        {
            this.fixture.Logger.Info("************************ MemoryStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(Fixture.StreamProviderName, Fixture.StreamNamespace, output,
                    null, true);
        }
    }
}
