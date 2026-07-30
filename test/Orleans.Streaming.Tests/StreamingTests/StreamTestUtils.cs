using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace UnitTests.StreamingTests
{
    internal class StreamTestUtils
    {
        public const string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AzureQueueProvider";

        internal static void LogStartTest(string testName, Guid streamId, string streamProviderName, ILogger logger, TestCluster siloHost)
        {
            SiloAddress primSilo = siloHost.Primary?.SiloAddress;
            SiloAddress secSilo = siloHost.SecondarySilos.FirstOrDefault()?.SiloAddress;
            logger.LogInformation(
                "\n\n**START********************** {TestName} ********************************* \n\n"
                + "Running with initial silos Primary={PrimarySilo} Secondary={SecondarySilo} StreamId={StreamId} StreamProviderName={StreamProviderName} \n\n",
                testName,
                primSilo,
                secSilo,
                streamId,
                streamProviderName);
        }

        internal static void LogEndTest(string testName, ILogger logger)
        {
            logger.LogInformation("\n\n--END------------------------ {TestName} --------------------------------- \n\n", testName);
        }

        internal static IStreamPubSub GetStreamPubSub(IInternalClusterClient client)
        {
            var runtime = client.ServiceProvider.GetRequiredService<IStreamProviderRuntime>();
            return runtime.PubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit);
        }

        internal static async Task CheckPubSubCounts(IInternalClusterClient client, ITestOutputHelper output, string when, int expectedPublisherCount, int expectedConsumerCount, Guid streamIdGuid, string streamProviderName, string streamNamespace)
        {
            var pubSub = GetStreamPubSub(client);
            var streamId = new QualifiedStreamId(streamProviderName, StreamId.Create(streamNamespace, streamIdGuid));

            await CheckPubSubCount(output, when, "ConsumerCount", streamId, streamProviderName, streamNamespace, expectedConsumerCount, () => pubSub.ConsumerCount(streamId));
            await CheckPubSubCount(output, when, "PublisherCount", streamId, streamProviderName, streamNamespace, expectedPublisherCount, () => pubSub.ProducerCount(streamId));
        }

        private static async Task<int> CheckPubSubCount(
            ITestOutputHelper output,
            string when,
            string countName,
            QualifiedStreamId streamId,
            string streamProviderName,
            string streamNamespace,
            int expectedCount,
            Func<Task<int>> getCount)
        {
            var count = await getCount();

            var message = string.Format(
                "{0} - {1} for stream {2} = {3}; expected {4}; provider={5}; namespace={6}",
                when,
                countName,
                streamId,
                count,
                expectedCount == -1 ? "not checked" : expectedCount,
                streamProviderName,
                streamNamespace);
            var prefix = expectedCount == -1 ? "Not-checked" : count == expectedCount ? "True" : "FALSE";
            output.WriteLine("--> {0}: {1}", prefix, message);

            if (expectedCount != -1)
            {
                Assert.True(count == expectedCount, message);
            }

            return count;
        }

        internal static void Assert_AreEqual(ITestOutputHelper output, int expected, int actual, string msg, params object[] args)
        {
            // expected == -1 means don't care / don't assert check value.
            string prefix = expected == -1 ? "Not-checked" : actual == expected ? "True" : "FALSE";
            string fmtMsg = string.Format("--> {0}: ", prefix) + string.Format(msg, args);
            output.WriteLine(fmtMsg);
            if (expected != -1)
            {
                Assert.Equal(expected, actual);
            }
        }
    }
}