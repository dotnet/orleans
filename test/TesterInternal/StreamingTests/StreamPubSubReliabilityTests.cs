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

namespace UnitTests.StreamingTests
{
    public class StreamPubSubReliabilityTests : OrleansTestingBase, IClassFixture<StreamPubSubReliabilityTests.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_StorageErrors.xml"),
                    LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
                    ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
                }, new TestingClientOptions
                {
                    ClientConfigFile = new FileInfo("ClientConfig_StreamProviders.xml")
                });
            }
        }

        private const string PubSubStoreProviderName = "PubSubStore";
        protected TestingSiloHost HostedCluster { get; private set; }

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;

        public StreamPubSubReliabilityTests(Fixture fixture)
        {
            HostedCluster = fixture.HostedCluster;
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;

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
            IStreamLifecycleConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, this.StreamNamespace, streamProviderName);

            // Producer
            IStreamLifecycleProducerGrain producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
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
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                ErrorInjectionStorageProvider provider = (ErrorInjectionStorageProvider)siloHandle.AppDomainTestHook.GetStorageProvider(providerName);
                provider.SetErrorInjection(errorInjectionPoint);
            }
        }
    }
}