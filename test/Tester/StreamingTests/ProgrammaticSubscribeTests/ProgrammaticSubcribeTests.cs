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
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            //set up subscription for 10 consumer grains
            await subGrain.SetupInitialStreamingSubscriptionForTests(StreamId, 10);

            var producer = this.fixture.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(StreamId.Guid, StreamId.Namespace, StreamId.ProviderName);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(2000));

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
                tasks.Add(TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, logger), _timeout));
            }
            await Task.WhenAll(tasks);

            tasks.Clear();
            foreach (var consumer in consumers)
            {
                tasks.Add(consumer.StopConsuming());
            }
            await Task.WhenAll(tasks);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_UnSubscribe()
        {
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            //set up subscription for one consumer grain
            var subscriptions = await subGrain.SetupInitialStreamingSubscriptionForTests(StreamId, 1);

            var producer = this.fixture.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(StreamId.Guid, StreamId.Namespace, StreamId.ProviderName);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            var subscription = subscriptions[0];
            // remove subscription
            await subGrain.RemoveSubscription(subscription);
            var numProducedWhenUnSub = await producer.GetNumberProduced();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();
            var consumer = this.fixture.GrainFactory.GetGrain<IStateless_ConsumerGrain>(subscription.GrainId.PrimaryKey);

            //wait for consumer to finish consuming
            await Task.Delay(TimeSpan.FromMilliseconds(2000));
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            Assert.True(numConsumed <= numProducedWhenUnSub);
            Assert.True(numConsumed < numProduced);
            await consumer.StopConsuming();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_Consumer_Producer_GetSubscriptions()
        {
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            //set up subscriptions
            var expectedSubscriptions = await subGrain.SetupInitialStreamingSubscriptionForTests(StreamId, 2);
            var expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
            var subscriptions = await subGrain.GetSubscriptions(StreamId);
            var subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
            Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));
        }

        //test utilities and statics
        public static string StreamProviderName = "SMSProvider";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
        public static FullStreamIdentity StreamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace",
            StreamProviderName);

        public static async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, IStateless_ConsumerGrain consumer, Logger logger)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
            return numProduced == numConsumed;
        }
    }
}
