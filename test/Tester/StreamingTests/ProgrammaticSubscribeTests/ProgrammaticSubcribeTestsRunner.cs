using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams.Core;
using TestExtensions;
using Xunit;
using UnitTests.GrainInterfaces;
using Orleans.TestingHost.Utils;
using UnitTests.Grains.ProgrammaticSubscribe;

namespace Tester.StreamingTests
{
    public abstract class ProgrammaticSubcribeTestsRunner 
    {
        private readonly BaseTestClusterFixture fixture;
        public const string StreamProviderName = "StreamProvider1";
        public const string StreamProviderName2 = "StreamProvider2";
        public ProgrammaticSubcribeTestsRunner(BaseTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [SkippableFact]
        public async Task Programmatic_Subscribe_Provider_WithExplicitPubsub_TryGetStreamSubscrptionManager()
        {
            var subGrain = this.fixture.HostedCluster.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            Assert.True(await subGrain.CanGetSubscriptionManager(StreamProviderName));
        }
        
        [SkippableFact]
        public async Task Programmatic_Subscribe_CanUseNullNamespace()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), null, StreamProviderName);
            await subscriptionManager.AddSubscription<IPassive_ConsumerGrain>(streamId,
                Guid.NewGuid());
            var subscriptions = await subscriptionManager.GetSubscriptions(streamId);
            await subscriptionManager.RemoveSubscription(streamId, subscriptions.First().SubscriptionId);
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_Subscribe()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            //set up subscription for 10 consumer grains
            var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
            var consumers = subscriptions.Select(sub => this.fixture.HostedCluster.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId)).ToList();

            var producer = this.fixture.HostedCluster.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            await producer.StopPeriodicProducing();
            
            var tasks = new List<Task>();
            foreach (var consumer in consumers)
            {
                tasks.Add(TestingUtils.WaitUntilAsync(lastTry => CheckCounters(new List<ITypedProducerGrain> { producer }, 
                    consumer, lastTry, this.fixture.Logger), _timeout));
            }
            await Task.WhenAll(tasks);

            //clean up test
            tasks.Clear();
            tasks = consumers.Select(consumer => consumer.StopConsuming()).ToList();
            await Task.WhenAll(tasks);
        }

        [SkippableFact(Skip= "https://github.com/dotnet/orleans/issues/5635")]
        public async Task StreamingTests_Consumer_Producer_UnSubscribe()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            //set up subscription for consumer grains
            var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 2);

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            //the subscription to remove
            var subscription = subscriptions[0];
            // remove subscription
            await subscriptionManager.RemoveSubscription(streamId, subscription.SubscriptionId);
            var numProducedWhenUnSub = await producer.GetNumberProduced();
            var consumerUnSub = this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(subscription.GrainId);
            var consumerNormal = this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(subscriptions[1].GrainId);
            //assert consumer grain's onAdd func got called.
            Assert.True((await consumerUnSub.GetCountOfOnAddFuncCalled()) > 0);
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProducedWhenUnSub, producer, lastTry), _timeout);
            await producer.StopPeriodicProducing();

            //wait for consumers to finish consuming
            await Task.Delay(TimeSpan.FromMilliseconds(2000));

            //assert normal consumer consumed equal to produced
            await TestingUtils.WaitUntilAsync(
            lastTry =>CheckCounters(new List<ITypedProducerGrain> { producer }, consumerNormal, lastTry, this.fixture.Logger), _timeout);

            //asert unsubscribed consumer consumed less than produced
            numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumerUnSub.GetNumberConsumed();
            Assert.True(numConsumed <= numProducedWhenUnSub);
            Assert.True(numConsumed < numProduced);

            // clean up test
            await consumerNormal.StopConsuming();
            await consumerUnSub.StopConsuming();
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_GetSubscriptions()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            //set up subscriptions
            var expectedSubscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 2);
            var expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
            var subscriptions = await subscriptionManager.GetSubscriptions(streamId);
            var subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
            Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));

             //remove one subscription
            await subscriptionManager.RemoveSubscription(streamId, expectedSubscriptions[0].SubscriptionId);
            expectedSubscriptions = expectedSubscriptions.GetRange(1, 1);
            subscriptions = await subscriptionManager.GetSubscriptions(streamId);
            expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
            subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
            Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));

            // clean up tests
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_ConsumerUnsubscribeOnAdd()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            //set up subscriptions
            await subscriptionManager.SetupStreamingSubscriptionForStream<IJerk_ConsumerGrain>(streamId, 10);
            //producer start producing 
            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingInt>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            await producer.StopPeriodicProducing();
            //wait for consumers to react
            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            //get subscription count now, should be all removed/unsubscribed 
            var subscriptions = await subscriptionManager.GetSubscriptions(streamId);
            Assert.True( subscriptions.Count<Orleans.Streams.Core.StreamSubscription>()== 0);
            // clean up tests
        }


        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5650")]
        public async Task StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            //set up subscription for 10 consumer grains
            var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
            var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId)).ToList();

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            // set up the new stream to subscribe, which produce strings
            var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName);
            var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            //register the consumer grain to second stream
            var tasks = consumers.Select(consumer => subscriptionManager.AddSubscription<IPassive_ConsumerGrain>(streamId2, consumer.GetPrimaryKey())).ToList();
            await Task.WhenAll(tasks);

            await producer2.StartPeriodicProducing();
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer2, lastTry), _timeout);
            await producer.StopPeriodicProducing();
            await producer2.StopPeriodicProducing();

            var tasks2 = new List<Task>();
            foreach (var consumer in consumers)
            {
                tasks2.Add(TestingUtils.WaitUntilAsync(lastTry => CheckCounters(new List<ITypedProducerGrain> { producer, producer2 },
                    consumer, lastTry, this.fixture.Logger), _timeout));
            }
            await Task.WhenAll(tasks);

            //clean up test
            tasks2.Clear();
            tasks2 = consumers.Select(consumer => consumer.StopConsuming()).ToList();
            await Task.WhenAll(tasks2);
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
        {
            var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            //set up subscription for 10 consumer grains
            var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
            var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId)).ToList();

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            // set up the new stream to subscribe, which produce strings
            var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName2);
            var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            //register the consumer grain to second stream
            var tasks = consumers.Select(consumer => subscriptionManager.AddSubscription<IPassive_ConsumerGrain>(streamId2, consumer.GetPrimaryKey())).ToList();
            await Task.WhenAll(tasks);

            await producer2.StartPeriodicProducing();
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer2, lastTry), _timeout);
            await producer.StopPeriodicProducing();
            await producer2.StopPeriodicProducing();

            var tasks2 = new List<Task>();
            foreach (var consumer in consumers)
            {
                tasks2.Add(TestingUtils.WaitUntilAsync(lastTry => CheckCounters(new List<ITypedProducerGrain> { producer, producer2 },
                    consumer, lastTry, this.fixture.Logger), _timeout));
            }
            await Task.WhenAll(tasks);

            //clean up test
            tasks2.Clear();
            tasks2 = consumers.Select(consumer => consumer.StopConsuming()).ToList();
            await Task.WhenAll(tasks2);
        }

        //test utilities and statics
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
       
        public static async Task<bool> ProducerHasProducedSinceLastCheck(int numProducedLastTime, ITypedProducerGrain producer, bool assertIsTrue)
        {
            var numProduced = await producer.GetNumberProduced();
            if (assertIsTrue)
            {
                throw new OrleansException($"Producer has not produced since last check");
            }
            else
            {
                return numProduced > numProducedLastTime;
            }
        }

        public static async Task<bool> CheckCounters(List<ITypedProducerGrain> producers, IPassive_ConsumerGrain consumer, bool assertIsTrue, ILogger logger)
        {
            int numProduced = 0;
            foreach (var p in producers)
            {
                numProduced += await p.GetNumberProduced();
            }
            var numConsumed = await consumer.GetNumberConsumed();
            logger.LogInformation("CheckCounters: numProduced = {ProducedCount}, numConsumed = {ConsumedCount}", numProduced, numConsumed);
            if (assertIsTrue)
            {
                Assert.Equal(numProduced, numConsumed);
                return true;
            }
            else
            {
                return numProduced == numConsumed;
            }
        }
    }

    public class SubscriptionManager
    {
        private IGrainFactory grainFactory;
        private IServiceProvider serviceProvider;
        private IStreamSubscriptionManager subManager;
        public SubscriptionManager(TestCluster cluster)
        {
            this.grainFactory = cluster.GrainFactory;
            this.serviceProvider = cluster.ServiceProvider;
            var admin = serviceProvider.GetRequiredService<IStreamSubscriptionManagerAdmin>();
            this.subManager = admin.GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
        }

        public async Task<List<StreamSubscription>> SetupStreamingSubscriptionForStream<TGrainInterface>(FullStreamIdentity streamIdentity, int grainCount)
            where TGrainInterface : IGrainWithGuidKey
        {
            var subscriptions = new List<StreamSubscription>();
            while (grainCount > 0)
            {
                var grainId = Guid.NewGuid();
                var grainRef = this.grainFactory.GetGrain<TGrainInterface>(grainId) as GrainReference;
                subscriptions.Add(await subManager.AddSubscription(streamIdentity.ProviderName, streamIdentity, grainRef));
                grainCount--;
            }
            return subscriptions;
        }

        public async Task<StreamSubscription> AddSubscription<TGrainInterface>(FullStreamIdentity streamId, Guid grainId)
            where TGrainInterface : IGrainWithGuidKey
        {
            var grainRef = this.grainFactory.GetGrain<TGrainInterface>(grainId) as GrainReference;
            var sub = await this.subManager
                .AddSubscription(streamId.ProviderName, streamId, grainRef);
            return sub;
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(FullStreamIdentity streamIdentity)
        {
            return subManager.GetSubscriptions(streamIdentity.ProviderName, streamIdentity);
        }

        public async Task RemoveSubscription(FullStreamIdentity streamId, Guid subscriptionId)
        {
            await subManager.RemoveSubscription(streamId.ProviderName, streamId, subscriptionId);
        }
    }
}
