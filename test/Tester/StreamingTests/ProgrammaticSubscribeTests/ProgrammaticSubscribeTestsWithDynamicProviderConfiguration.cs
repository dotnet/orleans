using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams.Core;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit.Abstractions;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    // this test suit mainly to prove subscriptions set up is decoupled from stream providers init
    // this test suit need to use TestClusterPerTest because each test has differnt provider config
    public class ProgrammaticSubscribeTestsWithDynamicProviderConfiguration : TestClusterPerTest
    {

        private ITestOutputHelper output;
        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            return new TestCluster(options);
        }

        public ProgrammaticSubscribeTestsWithDynamicProviderConfiguration(ITestOutputHelper output)
        {
            this.output = output;
        }

        private async Task<List<StreamSubscription>> SetupStreamingSubscriptionForStream<TGrainInterface>(IStreamSubscriptionManager subManager, IGrainFactory grainFactory,
            FullStreamIdentity streamIdentity, int grainCount)
            where TGrainInterface : IGrainWithGuidKey
        {
            //generate grain refs
            List<TGrainInterface> grains = new List<TGrainInterface>();
            while (grainCount > 0)
            {
                var grainId = Guid.NewGuid();
                var grain = grainFactory.GetGrain<TGrainInterface>(grainId);
                grains.Add(grain);
                grainCount--;
            }

            return await SetupStreamingSubscriptionForGrains(subManager, streamIdentity, grains);
        }

        private async Task<List<StreamSubscription>> SetupStreamingSubscriptionForGrains<TGrainInterface>(IStreamSubscriptionManager subManager,
            FullStreamIdentity streamIdentity, List<TGrainInterface> grains)
            where TGrainInterface : IGrainWithGuidKey
        {
            var subscriptions = new List<StreamSubscription>();
            foreach(var grain in grains)
            {
                var grainRef = grain as GrainReference;
                subscriptions.Add(await subManager.AddSubscription(streamIdentity.ProviderName, streamIdentity, grainRef));
            }
            return subscriptions;
        }
    }
}
