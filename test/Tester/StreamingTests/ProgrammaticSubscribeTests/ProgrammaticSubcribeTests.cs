using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;
using UnitTests.GrainInterfaces;
using Orleans.TestingHost.Utils;
using UnitTests.Grains.ProgrammaticSubscribe;

namespace Tester.StreamingTests
{
    public class ProgrammaticSubcribeTests : OrleansTestingBase, IClassFixture<ProgrammaticSubcribeTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName, false, true,
                    StreamPubSubType.ExplicitGrainBasedAndImplicit);
                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName2, false, true,
                   StreamPubSubType.ExplicitGrainBasedOnly);
                return new TestCluster(options);
            }
        }

        public ProgrammaticSubcribeTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task Programmatic_Subscribe_Provider_WithExplicitPubsub_CanGetSubscriptionManager()
        {
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            Assert.True(await subGrain.CanGetSubscriptionManager(StreamProviderName));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_Subscribe()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            //set up subscription for 10 consumer grains
            var subscriptions = await subGrain.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
            var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId.PrimaryKey)).ToList();

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
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

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_UnSubscribe()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            //set up subscription for consumer grains
            var subscriptions = await subGrain.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 2);

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingInt>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            //the subscription to remove
            var subscription = subscriptions[0];
            // remove subscription
            await subGrain.RemoveSubscription(subscription);
            var numProducedWhenUnSub = await producer.GetNumberProduced();
            var consumerUnSub = this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(subscription.GrainId.PrimaryKey);
            var consumerNormal = this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(subscriptions[1].GrainId.PrimaryKey);
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

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_GetSubscriptions()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            //set up subscriptions
            var expectedSubscriptions = await subGrain.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 2);
            var expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
            var subscriptions = await subGrain.GetSubscriptions(streamId);
            var subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
            Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));

             //remove one subscription
            await subGrain.RemoveSubscription(expectedSubscriptions[0]);
            expectedSubscriptions = expectedSubscriptions.GetRange(1, 1);
            subscriptions = await subGrain.GetSubscriptions(streamId);
            expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
            subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
            Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));

            // clean up tests
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_ConsumerUnsubscribeOnAdd()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            //set up subscriptions
            await subGrain.SetupStreamingSubscriptionForStream<IJerk_ConsumerGrain>(streamId, 10);
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
            var subscriptions = await subGrain.GetSubscriptions(streamId);
            Assert.True( subscriptions.Count<Orleans.Streams.Core.StreamSubscription>()== 0);
            // clean up tests
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_SubscribeToTwoStreamProcessDifferentType()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            //set up subscription for 10 consumer grains
            var subscriptions = await subGrain.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
            var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId.PrimaryKey)).ToList();

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingInt>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            // set up the new stream to subscribe, which produce strings
            var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName);
            var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            //register the consumer grain to second stream
            var tasks = consumers.Select(consumer => subGrain.AddSubscription<IPassive_ConsumerGrain>(streamId2, consumer.GetPrimaryKey())).ToList();
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

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            //set up subscription for 10 consumer grains
            var subscriptions = await subGrain.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
            var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId.PrimaryKey)).ToList();

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingInt>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            // set up the new stream to subscribe, which produce strings
            var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName2);
            var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            //register the consumer grain to second stream
            var tasks = consumers.Select(consumer => subGrain.AddSubscription<IPassive_ConsumerGrain>(streamId2, consumer.GetPrimaryKey())).ToList();
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
        public static string StreamProviderName = "SMSProvider";
        public static string StreamProviderName2 = "SMSProvider2";
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

        public static async Task<bool> CheckCounters(List<ITypedProducerGrain> producers, IPassive_ConsumerGrain consumer, bool assertIsTrue, Logger logger)
        {
            int numProduced = 0;
            foreach (var p in producers)
            {
                numProduced += await p.GetNumberProduced();
            }
            var numConsumed = await consumer.GetNumberConsumed();
            logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
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
}
