using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;

namespace UnitTests.StreamingTests
{
    internal class StreamTestUtils
    {
        public const string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AzureQueueProvider";

        internal static void LogStartTest(string testName, Guid streamId, string streamProviderName, Logger logger)
        {
            SiloAddress primSilo = TestingSiloHost.Primary.Silo.SiloAddress;
            SiloAddress secSilo = TestingSiloHost.Secondary != null ? TestingSiloHost.Secondary.Silo.SiloAddress : null;
            logger.Info("\n\n**START********************** {0} ********************************* \n\n"
                        + "Running with initial silos Primary={1} Secondary={2} StreamId={3} StreamType={4} \n\n",
                testName, primSilo, secSilo, streamId, streamProviderName);
        }

        internal static void LogEndTest(string testName, Logger logger)
        {
            logger.Info("\n\n--END------------------------ {0} --------------------------------- \n\n", testName);
        }

        internal static IStreamPubSub GetStreamPubSub()
        {
            return GrainClient.CurrentStreamProviderRuntime.PubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit);
        }

        internal static async Task CheckPubSubCounts(string when, int expectedPublisherCount, int expectedConsumerCount, Guid streamId, string streamProviderName, string streamNamespace)
        {
            var pubSub = GetStreamPubSub();

            int consumerCount = await pubSub.ConsumerCount(streamId, streamProviderName, streamNamespace);

            Assert_AreEqual(expectedConsumerCount, consumerCount, "{0} - ConsumerCount for stream {1} = {2}",
                when, streamId, consumerCount);

            int publisherCount = await pubSub.ProducerCount(streamId, streamProviderName, streamNamespace);

            Assert_AreEqual(expectedPublisherCount, publisherCount, "{0} - PublisherCount for stream {1} = {2}",
                when, streamId, publisherCount);
        }

        internal static void Assert_AreEqual(int expected, int actual, string msg, params object[] args)
        {
            // expected == -1 means don't care / don't assert check value.
            string prefix = expected == -1 ? "Not-checked" : actual == expected ? "True" : "FALSE";
            string fmtMsg = String.Format("--> {0}: ", prefix) + String.Format(msg, args);
            Console.WriteLine(fmtMsg);
            if (expected != -1)
            {
                Assert.AreEqual(expected, actual, msg, args);
            }
        }
    }
}