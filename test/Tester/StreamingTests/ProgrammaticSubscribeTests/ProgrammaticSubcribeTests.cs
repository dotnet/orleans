using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using Orleans.Streams.Core;
using UnitTests.GrainInterfaces;
using System.Threading;
using Orleans.TestingHost.Utils;
using UnitTests.Grains;

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
                    StreamPubSubType.ExplicitGrainBasedOnly);
                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProviderName, false, true,
                    StreamPubSubType.ExplicitGrainBasedOnly);

                return new TestCluster(options);
            }
        }

        public ProgrammaticSubcribeTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_Subscribe()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            //set up subscription for 10 consumer grains
            await subGrain.SetupInitialStreamingSubscriptionForTests(streamId, 10);

            var producer = this.fixture.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            await producer.StopPeriodicProducing();
            var grainIds = await subGrain.GetConsumerGrains();
            var consumers = new List<IStateless_ConsumerGrain>();
            foreach (var grainId in grainIds)
            {
                consumers.Add(this.fixture.GrainFactory.GetGrain<IStateless_ConsumerGrain>(grainId));
            }
            var tasks = new List<Task>();
            foreach (var consumer in consumers)
            {
                tasks.Add(TestingUtils.WaitUntilAsync(lastTry => SampleStreamingTests.CheckCounters(producer, consumer, lastTry, logger), _timeout));
            }
            await Task.WhenAll(tasks);

            tasks.Clear();
            foreach (var consumer in consumers)
            {
               tasks.Add(consumer.StopConsuming());
            }
            await Task.WhenAll(tasks);
            await subGrain.ClearStateAfterTesting();
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_UnSubscribe()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            //set up subscription for consumer grains
            var subscriptions = await subGrain.SetupInitialStreamingSubscriptionForTests(streamId, 2);

            var producer = this.fixture.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            await producer.StartPeriodicProducing();

            int numProduced = 0;
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProduced, producer, lastTry), _timeout);
            //the subscription to remove
            var subscription = subscriptions[0];
            // remove subscription
            await subGrain.RemoveSubscription(subscription);
            var numProducedWhenUnSub = await producer.GetNumberProduced();
            await TestingUtils.WaitUntilAsync(lastTry => ProducerHasProducedSinceLastCheck(numProducedWhenUnSub, producer, lastTry), _timeout);
            await producer.StopPeriodicProducing();
            var consumerUnSub = this.fixture.GrainFactory.GetGrain<IStateless_ConsumerGrain>(subscription.GrainId.PrimaryKey);
            var consumerNormal = this.fixture.GrainFactory.GetGrain<IStateless_ConsumerGrain>(subscriptions[1].GrainId.PrimaryKey);
            //wait for consumers to finish consuming
            await Task.Delay(TimeSpan.FromMilliseconds(2000));
            numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumerUnSub.GetNumberConsumed();
            //asert unsubscribed consumer consumed less than produced
            Assert.True(numConsumed <= numProducedWhenUnSub);
            Assert.True(numConsumed < numProduced);
            //assert normal consumer consumed equal to produced
            await TestingUtils.WaitUntilAsync(
            lastTry => SampleStreamingTests.CheckCounters(producer, consumerNormal, lastTry, logger), _timeout);
            await consumerNormal.StopConsuming();
            await consumerUnSub.StopConsuming();
            await subGrain.ClearStateAfterTesting();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_GetSubscriptions()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            //set up subscriptions
            var expectedSubscriptions = await subGrain.SetupInitialStreamingSubscriptionForTests(streamId, 2);
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

            await subGrain.ClearStateAfterTesting();
        }

        //test utilities and statics
        public static string StreamProviderName = "SMSProvider";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
       
        public static async Task<bool> ProducerHasProducedSinceLastCheck(int numProducedLastTime, ISampleStreaming_ProducerGrain producer, bool assertIsTrue)
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
    }
}
