using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams.Core;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.Grains.ProgrammaticSubscribe;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UnitTests.GrainInterfaces;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
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
        private static string StreamProviderName = "SMSProvider";
        private static string StreamProviderName2 = "SMSProvider2";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
        public ProgrammaticSubscribeTestsWithDynamicProviderConfiguration(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task Programmatic_Subscribe_UsingClientSideSubscriptionManager_UsingDynamicProviderConfig()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subManager = this.Client.ServiceProvider.GetService<IStreamSubscriptionManagerAdmin>()
                .GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            //set up stream subscriptions for grains
            var subscriptions = await SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(subManager, this.GrainFactory, streamId, 10);
            var consumers = subscriptions.Select(sub => this.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId.PrimaryKey)).ToList();

            // configure stream provider after subscriptions set up
            await AddSimpleStreamProviderAndUpdate(new List<String>() { StreamProviderName });

            //set up producer
            var producer = this.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubcribeTests.ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            await producer.StopPeriodicProducing();

            var tasks = new List<Task>();
            foreach (var consumer in consumers)
            {
                tasks.Add(TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubcribeTests.CheckCounters(new List<ITypedProducerGrain> { producer },
                    consumer, lastTry, this.Logger), _timeout));
            }
            await Task.WhenAll(tasks);

            //clean up test
            tasks.Clear();
            tasks = consumers.Select(consumer => consumer.StopConsuming()).ToList();
            await Task.WhenAll(tasks);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task Programmatic_Subscribe_DynamicAddNewStreamProvider_WhenConsuming()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subManager = this.Client.ServiceProvider.GetService<IStreamSubscriptionManagerAdmin>()
                .GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            //set up stream subscriptions for grains
            var subscriptions = await SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(subManager, this.GrainFactory, streamId, 10);
            var consumers = subscriptions.Select(sub => this.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId.PrimaryKey)).ToList();

            // configure stream provider1 
            await AddSimpleStreamProviderAndUpdate(new List<String>() { StreamProviderName });

            //set up producer1 
            var producer = this.GrainFactory.GetGrain<ITypedProducerGrainProducingInt>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubcribeTests.ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);

            //set up stream2 and StreamProvider2
            await AddSimpleStreamProviderAndUpdate(new List<String>() { StreamProviderName2 });
            var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName2);
            await SetupStreamingSubscriptionForGrains<IPassive_ConsumerGrain>(subManager, streamId2, consumers);

            //set up producer2 to produce to stream2
            var producer2 = this.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            await producer2.StartPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubcribeTests.ProducerHasProducedSinceLastCheck(numProduced, producer2, lastTry), _timeout);

            //stop producing
            await producer2.StopPeriodicProducing();
            await producer.StopPeriodicProducing();

            var tasks = new List<Task>();
            foreach (var consumer in consumers)
            {
                tasks.Add(TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubcribeTests.CheckCounters(new List<ITypedProducerGrain> { producer, producer2 },
                    consumer, lastTry, this.Logger), _timeout));
            }
            await Task.WhenAll(tasks);

            //clean up test
            tasks.Clear();
            tasks = consumers.Select(consumer => consumer.StopConsuming()).ToList();
            await Task.WhenAll(tasks);
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

        private async Task AddSimpleStreamProviderAndUpdate(ICollection<string> streamProviderNames)
        {
            foreach (string providerName in streamProviderNames)
            {
                this.HostedCluster.ClusterConfiguration.AddSimpleMessageStreamProvider(providerName, false, true,
                    StreamPubSubType.ExplicitGrainBasedAndImplicit);
            }
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            var siloAddresses = this.HostedCluster.GetActiveSilos().Select(siloHandle => siloHandle.SiloAddress).ToArray();
            await mgmtGrain.UpdateStreamProviders(siloAddresses, this.HostedCluster.ClusterConfiguration.Globals.ProviderConfigurations);
        }

    }
}
