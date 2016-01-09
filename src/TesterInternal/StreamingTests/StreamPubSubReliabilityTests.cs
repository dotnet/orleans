using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.StorageTests;
using UnitTests.Tester;
//using UnitTests.Streaming.Reliability;

namespace UnitTests.StreamingTests
{
    [TestClass]
    [DeploymentItem("Config_StorageErrors.xml")]
    [DeploymentItem("ClientConfig_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    public class StreamPubSubReliabilityTests : UnitTestSiloHost
    {
        public TestContext TestContext { get; set; }
        protected static readonly TestingSiloOptions SiloRunOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            SiloConfigFile = new FileInfo("Config_StorageErrors.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        protected static readonly TestingClientOptions ClientRunOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfig_StreamProviders.xml")
        };

        private const string PubSubStoreProviderName = "PubSubStore";

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;

        public StreamPubSubReliabilityTests()
            : base(SiloRunOptions, ClientRunOptions)
        { }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            //ResetAllAdditionalRuntimes();
            //ResetDefaultRuntimes();
            StopAllSilos();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            logger.Info("TestInitialize - {0}", TestContext.TestName);
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;

            SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.None);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            logger.Info("TestCleanup - {0} - Test completed: Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
            StreamId = default(Guid);
            StreamProviderName = null;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_Baseline()
        {
            await Test_PubSub_Stream(StreamProviderName, StreamId);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        // TODO: [ExpectedException(typeof(StorageProviderInjectedError))]
        [ExpectedException(typeof(OrleansException))]
        public async Task PubSub_Store_ReadError()
        {
            // Expected behaviour: Underlying error StorageProviderInjectedError returned to caller
            //
            // Actual behaviour: Rather cryptic error OrleansException returned, mentioning 
            //                   root cause problem "Failed SetupActivationState" in message text, 
            //                   but no more details or stack trace.

            try
            {
                SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.BeforeRead);

                await Test_PubSub_Stream(StreamProviderName, StreamId);
            }
            catch (AggregateException ae)
            {
                Console.WriteLine("Received error = {0}", ae);

                Exception exc = ae.GetBaseException();
                if (exc.InnerException != null)
                    exc = exc.GetBaseException();
                Console.WriteLine("Returning error = {0}", exc);
                throw exc;
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        [ExpectedException(typeof(StorageProviderInjectedError))]
        public async Task PubSub_Store_WriteError()
        {
            try
            {
                SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.BeforeWrite);

                await Test_PubSub_Stream(StreamProviderName, StreamId);
            }
            catch (AggregateException ae)
            {
                Console.WriteLine("Received error = {0}", ae);

                Exception exc = ae.GetBaseException();
                if (exc.InnerException != null)
                    exc = exc.GetBaseException();
                Console.WriteLine("Returning error = {0}", exc);
                throw exc;
            }
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

            Assert.IsTrue(received1 > 1, "Received count for consumer {0} is too low = {1}", consumer, received1);

            // Unsubscribe
            await consumer.ClearGrain();

            // Send one more message
            await producer.SendItem(2);


            int received2 = await consumer.GetReceivedCount();

            Assert.AreEqual(0, received2, "Received count for consumer {0} is wrong = {1}", consumer, received2);

        }

        private void SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                ErrorInjectionStorageProvider provider = (ErrorInjectionStorageProvider) siloHandle.Silo.TestHook.GetStorageProvider(providerName);
                provider.SetErrorInjection(errorInjectionPoint);
            }
        }
    }
}