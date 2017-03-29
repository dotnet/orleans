﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StorageTests;
using Xunit;
using Tester;

namespace UnitTests.StreamingTests
{
    public class StreamPubSubReliabilityTests : OrleansTestingBase, IClassFixture<StreamPubSubReliabilityTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(initialSilosCount: 4);

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>(PubSubStoreProviderName);

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, fireAndForgetDelivery: false);

                options.ClusterConfiguration.Globals.MaxResendCount = 0;
                options.ClusterConfiguration.Globals.ResponseTimeout = TimeSpan.FromSeconds(30);

                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, fireAndForgetDelivery: false);

                options.ClientConfiguration.ClientSenderBuckets = 8192;
                options.ClientConfiguration.ResponseTimeout = TimeSpan.FromSeconds(30);
                options.ClientConfiguration.MaxResendCount = 0;

                return new TestCluster(options);
            }
        }

        private const string PubSubStoreProviderName = "PubSubStore";

        public IGrainFactory GrainFactory { get; }

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;
        protected TestCluster HostedCluster;

        public StreamPubSubReliabilityTests(Fixture fixture)
        {
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;
            this.HostedCluster = fixture.HostedCluster;
            this.GrainFactory = fixture.GrainFactory;
            SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.None);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_Baseline()
        {
            await Test_PubSub_Stream(StreamProviderName, StreamId);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_ReadError()
        {
            // Expected behaviour: Underlying error StorageProviderInjectedError returned to caller
            //
            // Actual behaviour: Rather cryptic error OrleansException returned, mentioning 
            //                   root cause problem "Failed SetupActivationState" in message text, 
            //                   but no more details or stack trace.

            SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.BeforeRead);

            // TODO: expect StorageProviderInjectedError directly instead of OrleansException
            await Assert.ThrowsAsync<OrleansException>(() =>
                Test_PubSub_Stream(StreamProviderName, StreamId));
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_WriteError()
        {
            SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.BeforeWrite);

            var exception = await Assert.ThrowsAsync<OrleansException>(() =>
                Test_PubSub_Stream(StreamProviderName, StreamId));

            Assert.IsAssignableFrom<StorageProviderInjectedError>(exception.InnerException);
        }

        private async Task Test_PubSub_Stream(string streamProviderName, Guid streamId)
        {
            // Consumer
            IStreamLifecycleConsumerGrain consumer = this.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, this.StreamNamespace, streamProviderName);

            // Producer
            IStreamLifecycleProducerGrain producer = this.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(StreamId, this.StreamNamespace, streamProviderName);

            await producer.SendItem(1);

            int received1 = await consumer.GetReceivedCount();

            Assert.True(received1 > 1, $"Received count for consumer {consumer} is too low = {received1}");

            // Unsubscribe
            await consumer.ClearGrain();

            // Send one more message
            await producer.SendItem(2);


            int received2 = await consumer.GetReceivedCount();

            Assert.Equal(0, received2);  // $"Received count for consumer {consumer} is wrong = {received2}"

        }

        private void SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            IManagementGrain mgmtGrain = this.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            mgmtGrain.SendControlCommandToProvider(typeof(ErrorInjectionStorageProvider).FullName,
                providerName, (int)MockStorageProvider.Commands.SetErrorInjection, errorInjectionPoint).Wait();
        }
    }
}