using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams.Core;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Streams;
using TestExtensions;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit.Abstractions;
using Orleans.Hosting;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    // this test suit mainly to prove subscriptions set up is decoupled from stream providers init
    // this test suit need to use TestClusterPerTest because each test has differnt provider config
    public class ProgrammaticSubscribeTestsWithDynamicProviderConfiguration : TestClusterPerTest
    {

        private ITestOutputHelper output;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore");
            }
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
