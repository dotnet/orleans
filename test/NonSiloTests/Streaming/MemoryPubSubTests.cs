using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.Streams.PubSub;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StreamingTests
{
    [TestCategory("Streaming")]
    public class MemoryPubSubTests
    {
        private readonly ITestOutputHelper output;
        private static string streamProvider = "TestProvider";
        public MemoryPubSubTests(ITestOutputHelper output)
        {
            this.output = output;
            LogManager.Initialize(new NodeConfiguration());
        }
        #region Register/Unregister producer
        [Fact, TestCategory("BVT")]
        public async Task SameStream_CanRegisterProducersConcurrently()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            List <Task> tasks = new List<Task>();
            int numOfProducers = 100;
            var producers = GenerateProducers(numOfProducers);
            foreach (var producer in producers)
            {
                tasks.Add(memoryPubSub.RegisterProducer(streamId, streamProvider, producer));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(numOfProducers, await memoryPubSub.ProducerCount(streamId.Guid, streamId.ProviderName, streamId.Namespace));
        }

        [Fact, TestCategory("BVT")]
        public async Task SameStream_CanUnRegisterProducersConcurrently()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            List<Task> tasks = new List<Task>();
            int numOfProducers = 100;
            int unregisterNumOfProducers = 40;
            var producers = GenerateProducers(numOfProducers);
            foreach (var producer in producers)
            {
                tasks.Add(memoryPubSub.RegisterProducer(streamId, streamProvider, producer));
            }
            for (int i = 0; i < unregisterNumOfProducers; i++)
            {
                tasks.Add(memoryPubSub.UnregisterProducer(streamId, streamProvider, producers[i]));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(numOfProducers - unregisterNumOfProducers, await memoryPubSub.ProducerCount(streamId.Guid, streamId.ProviderName, streamId.Namespace));
        }

        [Fact, TestCategory("BVT")]
        public async Task SameStream_RegisterProducerWillReturnItsConsumers()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var consumer = new DummyStreamConsumerExtension();
            var producer = new DummyStreamProducerExtension();
            var subStates = await memoryPubSub.RegisterProducer(streamId, streamProvider, producer);
            Assert.Equal(0, subStates.Count);
            var subscriptionIds = GenerateSubsciptionIds(100);
            var tasks = new List<Task>();
            foreach (var subId in subscriptionIds)
            {
                tasks.Add(memoryPubSub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            await Task.WhenAll(tasks);
            subStates = await memoryPubSub.RegisterProducer(streamId, streamProvider, producer);
            Assert.Equal(subscriptionIds.Count, subStates.Count);
            var registeredSubIds = new HashSet<GuidId>();
            foreach (var subState in subStates)
            {
                registeredSubIds.Add(subState.SubscriptionId);
            }
            Assert.True(new HashSet<GuidId>(subscriptionIds).SetEquals(registeredSubIds));
        }

        [Fact, TestCategory("BVT")]
        public async Task DifferentStream_CanRegisterProducersConcurrently()
        {
            var memoryPubSub = new MemoryPubSub();

            var firstStreamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var secondStreamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            List<Task> tasks = new List<Task>();
            int numOfProducers = 100;
            int numOfProducers2 = 200;
            var producers = GenerateProducers(numOfProducers);
            var producers2 = GenerateProducers(numOfProducers2);
            foreach (var producer in producers)
            {
                tasks.Add(memoryPubSub.RegisterProducer(firstStreamId, streamProvider, producer));
            }

            foreach (var producer in producers2)
            {
                tasks.Add(memoryPubSub.RegisterProducer(secondStreamId, streamProvider, producer));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(numOfProducers, await memoryPubSub.ProducerCount(firstStreamId.Guid, firstStreamId.ProviderName, firstStreamId.Namespace));
            Assert.Equal(numOfProducers2, await memoryPubSub.ProducerCount(secondStreamId.Guid, secondStreamId.ProviderName, secondStreamId.Namespace));
        }
        #endregion

        #region Register/Unregister Consumer
        [Fact, TestCategory("BVT")]
        public async Task SameStream_CanRegisterConsumerCocurrently()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var consumer =
                new DummyStreamConsumerExtension();
            List<Task> tasks = new List<Task>();
            int numOfSubscriptions = 100;
            var expectedSubscriptionIds = GenerateSubsciptionIds(numOfSubscriptions);
            foreach (var subId in expectedSubscriptionIds)
            {
                tasks.Add(memoryPubSub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(numOfSubscriptions, await memoryPubSub.ConsumerCount(streamId.Guid, streamId.ProviderName, streamId.Namespace));
            // assert registered subscriptionId is the same
            var registerdSubscriptionIds = await memoryPubSub.GetAllSubscriptions(streamId, consumer);
            Assert.True(new HashSet<GuidId>(expectedSubscriptionIds).SetEquals(registerdSubscriptionIds));
        }

        [Fact, TestCategory("BVT")]
        public async Task SameStream_CanUnRegisterConsumerCocurrently()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var consumer = new DummyStreamConsumerExtension();
            List<Task> tasks = new List<Task>();
            int numOfSubscriptions = 100;
            int unRegisterNum = 40;
            var subscriptionIds = GenerateSubsciptionIds(numOfSubscriptions);
            foreach (var subId in subscriptionIds)
            {
                tasks.Add(memoryPubSub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            for (int i = 0; i < unRegisterNum; i++)
            {
                tasks.Add(memoryPubSub.UnregisterConsumer(subscriptionIds[i], streamId, streamProvider));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(numOfSubscriptions - unRegisterNum, await memoryPubSub.ConsumerCount(streamId.Guid, streamId.ProviderName, streamId.Namespace));
            //assert registered sunscription is the same
            var registerdSubscriptionIds = await memoryPubSub.GetAllSubscriptions(streamId, consumer);
            var expectedSubscriptionIds = subscriptionIds.GetRange(unRegisterNum, subscriptionIds.Count - unRegisterNum);
            Assert.True(new HashSet<GuidId>(expectedSubscriptionIds).SetEquals(registerdSubscriptionIds));
        }

        [Fact, TestCategory("BVT")]
        public async Task DifferentStream_CanRegisterConsumerCocurrently()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var streamId2 = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var consumer =
                new DummyStreamConsumerExtension();
            var consumer2 = new DummyStreamConsumerExtension();
            List<Task> tasks = new List<Task>();
            int numOfSubscriptions = 100;
            int numOfSubscriptions2 = 40;
            var expectedSubscriptionIds = GenerateSubsciptionIds(numOfSubscriptions);
            foreach (var subId in expectedSubscriptionIds)
            {
                tasks.Add(memoryPubSub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            var expectedSubscriptionIds2 = GenerateSubsciptionIds(numOfSubscriptions2);
            foreach (var subId in expectedSubscriptionIds2)
            {
                tasks.Add(memoryPubSub.RegisterConsumer(subId, streamId2, streamProvider, consumer2, null));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(numOfSubscriptions, await memoryPubSub.ConsumerCount(streamId.Guid, streamId.ProviderName, streamId.Namespace));
            Assert.Equal(numOfSubscriptions2, await memoryPubSub.ConsumerCount(streamId2.Guid, streamId2.ProviderName, streamId2.Namespace));
            // assert registered subscriptionId is the same
            var registerdSubscriptionIds = await memoryPubSub.GetAllSubscriptions(streamId, consumer);
            Assert.True(new HashSet<GuidId>(expectedSubscriptionIds).SetEquals(registerdSubscriptionIds));
            var registerdSubscriptionIds2 = await memoryPubSub.GetAllSubscriptions(streamId2, consumer2);
            Assert.True(new HashSet<GuidId>(expectedSubscriptionIds2).SetEquals(registerdSubscriptionIds2));
        }
        #endregion

        #region Subscription related

        [Fact, TestCategory("BVT")]
        public async Task RegisterConsumer_WouldNotifyProducerOfNewSubscription()
        {
            var memoryPubsub = new MemoryPubSub();
            var producer = new DummyStreamProducerExtension();
            var consumer = new DummyStreamConsumerExtension();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var numOfSubscriptions = 10;
            var expectedSubscriptionIds = GenerateSubsciptionIds(numOfSubscriptions);
            var tasks = new List<Task>();
            //register producer
            await memoryPubsub.RegisterProducer(streamId, streamProvider, producer);
            foreach (var subId in expectedSubscriptionIds)
            {
                // register consumer
                tasks.Add(memoryPubsub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            await Task.WhenAll(tasks);
            Assert.True(new HashSet<GuidId>(expectedSubscriptionIds).SetEquals(producer.SubscriptionIds));
        }

        [Fact, TestCategory("BVT")]
        public async Task UnRegisterConsumer_WouldNotifyProducerToRemoveRelatedSubscription()
        {
            var memoryPubsub = new MemoryPubSub();
            var producer = new DummyStreamProducerExtension();
            var consumer = new DummyStreamConsumerExtension();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var numOfSubscriptions = 10;
            var newSubscriptionIds = GenerateSubsciptionIds(numOfSubscriptions);
            var tasks = new List<Task>();
            //register producer
            await memoryPubsub.RegisterProducer(streamId, streamProvider, producer);
            foreach (var subId in newSubscriptionIds)
            {
                // register consumer
                tasks.Add(memoryPubsub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            var numOfRemovedSubscription = 5;
            for (int i = 0; i < numOfRemovedSubscription; i++)
            {
                tasks.Add(memoryPubsub.UnregisterConsumer(newSubscriptionIds[i], streamId, streamProvider));
            }
            await Task.WhenAll(tasks);
            var expectedSubscriptionIds = newSubscriptionIds.GetRange(numOfRemovedSubscription,
                newSubscriptionIds.Count - numOfRemovedSubscription);
            Assert.True(new HashSet<GuidId>(expectedSubscriptionIds).SetEquals(producer.SubscriptionIds));
        }

        [Fact, TestCategory("BVT")]
        public void CreateSubscrptionId_CanCreateDifferentIds()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            int numOfSubscriptionId = 1000;
            var consumer = new DummyStreamConsumerExtension();
            var subscriptionIds = new List<GuidId>();
            for (int i = 0; i < numOfSubscriptionId; i++)
            {
                subscriptionIds.Add(memoryPubSub.CreateSubscriptionId(streamId, consumer));
            }
            Random rnd = new Random();
            int idIndex1 = rnd.Next(0, numOfSubscriptionId);
            int idIndex2 = rnd.Next(0, numOfSubscriptionId);
            Assert.NotEqual(subscriptionIds[idIndex1], subscriptionIds[idIndex2]);
        }

        [Fact, TestCategory("BVT")]
        public async Task GetAllSubscription_WontReturnFaultSubscription()
        {
            var memoryPubsub = new MemoryPubSub();
            var producer = new DummyStreamProducerExtension();
            var consumer = new DummyStreamConsumerExtension();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var numOfSubscriptions = 10;
            var subscriptionIds = GenerateSubsciptionIds(numOfSubscriptions);
            var faultSubscriptionIdIndex = 0;
            var tasks = new List<Task>();
            //register producer
            await memoryPubsub.RegisterProducer(streamId, streamProvider, producer);
            foreach (var subId in subscriptionIds)
            {
                // register consumer
                tasks.Add(memoryPubsub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            tasks.Add(memoryPubsub.FaultSubscription(streamId, subscriptionIds[faultSubscriptionIdIndex]));
            await Task.WhenAll(tasks);
            var allSubscriptions = await memoryPubsub.GetAllSubscriptions(streamId, consumer);
            var expectedSubscriptions = subscriptionIds.GetRange(faultSubscriptionIdIndex + 1 , subscriptionIds.Count - 1);
            Assert.True(new HashSet<GuidId>(expectedSubscriptions).SetEquals(new HashSet<GuidId>(allSubscriptions)));
            Assert.False(new HashSet<GuidId>(allSubscriptions).Contains(subscriptionIds[faultSubscriptionIdIndex]));
        }

        [Fact, TestCategory("BVT")]
        public async Task RegisterConsumerWithAFaultedSubscription_WouldThrowFaultedSubscriptionException()
        {
            var memoryPubsub = new MemoryPubSub();
            var consumer = new DummyStreamConsumerExtension();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var subscriptionId = memoryPubsub.CreateSubscriptionId(streamId, consumer);
            //register consumer
            await memoryPubsub.RegisterConsumer(subscriptionId, streamId, streamProvider, consumer, null);
            //fault subscription
            await memoryPubsub.FaultSubscription(streamId, subscriptionId);
            await Assert.ThrowsAsync<FaultedSubscriptionException>(
                () => memoryPubsub.RegisterConsumer(subscriptionId, streamId, streamProvider, consumer, null));
        }

        [Fact, TestCategory("BVT")]
        public async Task UnRegisterConsumerWithAFaultedSubscription_WouldThrowFaultedSubscriptionException()
        {
            var memoryPubsub = new MemoryPubSub();
            var consumer = new DummyStreamConsumerExtension();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var subscriptionId = memoryPubsub.CreateSubscriptionId(streamId, consumer);
            //register consumer
            await memoryPubsub.RegisterConsumer(subscriptionId, streamId, streamProvider, consumer, null);
            //fault subscription
            await memoryPubsub.FaultSubscription(streamId, subscriptionId);
            await Assert.ThrowsAsync<FaultedSubscriptionException>(
                () => memoryPubsub.UnregisterConsumer(subscriptionId, streamId, streamProvider));
        }

        [Fact, TestCategory("BVT")]
        public async Task RegisterProducerWontReturnFaultSubscription()
        {
            var memoryPubSub = new MemoryPubSub();
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), streamProvider, null);
            var consumer = new DummyStreamConsumerExtension();
            var producer = new DummyStreamProducerExtension();
            var subscriptionIds = GenerateSubsciptionIds(100);
            var faultSubIndex = 0;
            var tasks = new List<Task>();
            foreach (var subId in subscriptionIds)
            {
                tasks.Add(memoryPubSub.RegisterConsumer(subId, streamId, streamProvider, consumer, null));
            }
            await Task.WhenAll(tasks);
            await memoryPubSub.FaultSubscription(streamId, subscriptionIds[faultSubIndex]);
            var subStates = await memoryPubSub.RegisterProducer(streamId, streamProvider, producer);
            var expectedSubscriptionCount = subscriptionIds.Count - 1;
            var expectedSubIds = subscriptionIds.GetRange(faultSubIndex + 1, expectedSubscriptionCount);
            Assert.Equal(expectedSubscriptionCount, subStates.Count);
            var registeredSubIds = new HashSet<GuidId>();
            foreach (var subState in subStates)
            {
                registeredSubIds.Add(subState.SubscriptionId);
            }
            
            Assert.True(new HashSet<GuidId>(expectedSubIds).SetEquals(registeredSubIds));
        }

        #endregion
        #region Private classes and methods
        private List<DummyStreamProducerExtension> GenerateProducers(int n)
        {
            List<DummyStreamProducerExtension> producers = new List<DummyStreamProducerExtension>();
            for (int i = 0; i < n; i++)
            {
                producers.Add(new DummyStreamProducerExtension());
            }

            return producers;
        }
        private List<GuidId> GenerateSubsciptionIds(int n)
        {
            List<GuidId> ids = new List<GuidId>();
            for (int i = 0; i < n; i++)
            {
                ids.Add(GuidId.GetNewGuidId());
            }

            return ids;
        }

        private class DummyStreamConsumerExtension: IStreamConsumerExtension
        {

            public Task<StreamHandshakeToken> DeliverImmutable(GuidId subscriptionId, Immutable<object> item,
                StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
            {
                throw new NotImplementedException();
            }

            public Task<StreamHandshakeToken> DeliverMutable(GuidId subscriptionId, object item,
                StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
            {
                throw new NotImplementedException();
            }

            public Task<StreamHandshakeToken> DeliverBatch(GuidId subscriptionId, Immutable<IBatchContainer> item,
                StreamHandshakeToken handshakeToken)
            {
                throw new NotImplementedException();
            }

            public Task CompleteStream(GuidId subscriptionId)
            {
                return TaskDone.Done;
            }

            public Task ErrorInStream(GuidId subscriptionId, Exception exc)
            {
                return TaskDone.Done;
            }

            public Task<StreamHandshakeToken> GetSequenceToken(GuidId subscriptionId)
            {
                throw new NotImplementedException();
            }
        }

        private class DummyStreamProducerExtension : IStreamProducerExtension
        {
            private readonly Guid id;
            private HashSet<GuidId> subscriptionIds;
            public HashSet<GuidId> SubscriptionIds { get { return subscriptionIds;} }

            public DummyStreamProducerExtension()
            {
                id = Guid.NewGuid();
                subscriptionIds = new HashSet<GuidId>();
            }

            public Task AddSubscriber(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer,
                IStreamFilterPredicateWrapper filter)
            {
                subscriptionIds.Add(subscriptionId);
                return TaskDone.Done;
            }

            public Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId)
            {
                subscriptionIds.Remove(subscriptionId);
                return TaskDone.Done;
            }
        }
        #endregion
    }
}
