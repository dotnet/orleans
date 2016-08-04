using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Memory;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class MemoryStreamProviderClientTests : TestClusterPerTest
    {
        private const string StreamProviderName = "MemoryStreamProvider";
        private const string StreamNamespace = "StreamNamespace";

        private static readonly MemoryAdapterConfig ProviderConfig = new MemoryAdapterConfig(StreamProviderName, 1);

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public MemoryStreamProviderClientTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(1);
            AdjustConfig(options.ClusterConfiguration);
            AdjustConfig(options.ClientConfiguration);
            return new TestCluster(options);
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
         
        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MemoryStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ MemoryStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(StreamProviderName, StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MemoryStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ MemoryStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(StreamProviderName, StreamNamespace, output,
                    null, true);
        }
    }
}
