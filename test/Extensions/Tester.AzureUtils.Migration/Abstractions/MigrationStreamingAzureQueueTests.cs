using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationStreamingAzureQueueTests : MigrationBaseTests
    {
        const int baseId = 800;

        protected MigrationStreamingAzureQueueTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [SkippableFact]
        public async Task Streaming_PushesDataIntoPredeterminedAzureQueue_AndReceivesViaExplicitSubscription()
        {
            var providerName = MigrationStreamingAzureQueueSetup.StreamProviderName;
            var streamId = Guid.NewGuid();
            var streamNamespace = $"test-{baseId}-123";
            var data = GenerateStreamData();

            var streamProvider = ServiceProvider.GetRequiredServiceByName<IStreamProvider>(providerName);
            var stream = streamProvider.GetStream<StreamDataType>(streamId, streamNamespace);

            // subscribe to receive the message.
            // there is no way to explicitly get the message, because scheduler will pick it up by itself.
            var receivedMessages = new List<StreamDataType>();
            var subscriptionHandle = await stream.SubscribeAsync((message, token) =>
            {
                receivedMessages.Add(message);
                return Task.CompletedTask;
            });

            var persistentStreamProvider = (PersistentStreamProvider)streamProvider;
            var adapter = (AzureQueueAdapter)persistentStreamProvider.queueAdapter;
            await adapter.QueueMessageAsync(streamId, streamNamespace, data, token: null, requestContext: new Dictionary<string, object>());

            // wait until background worker reads the message
            await Task.Delay(TimeSpan.FromSeconds(5));

            // verify the message was received
            Assert.Single(receivedMessages);
            var receivedMessage = receivedMessages[0];
            Assert.Equal(data.Id, receivedMessage.Id);
            Assert.Equal(data.Name, receivedMessage.Name);
            Assert.Equal(data.Version, receivedMessage.Version);

            await subscriptionHandle.UnsubscribeAsync();
        }

        private static StreamDataType GenerateStreamData()
        {
            return new StreamDataType
            {
                Id = Guid.NewGuid().ToString(),
                Name = "TestStreamData",
                Version = 1
            };
        }
    }

    public class StreamDataType
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Version { get; set; }
    }
}
