using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationStreamingAzureQueueTests : MigrationBaseTests
    {
        const int baseId = 800;
        static Random rnd = new();

        protected MigrationStreamingAzureQueueTests(BaseAzureTestClusterFixture fixture)
            : base(fixture)
        {
        }

        [SkippableFact]
        public async Task Streaming_PushesDefaultIntoPredeterminedAzureQueue_AndReceivesViaExplicitSubscription()
        {
            var providerName = MigrationStreamingAzureQueueSetup.StreamProviderName;
            var streamId = Guid.NewGuid();
            var streamNamespace = $"test-{baseId}-{rnd.Next(0, 1000)}";
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

        [SkippableFact]
        public async Task Streaming_PushBase64IntoPredeterminedAzureQueue_AndReceivesViaExplicitSubscription()
        {
            var providerName = MigrationStreamingAzureQueueSetup.StreamProviderName;
            var streamId = Guid.NewGuid();
            var streamNamespace = $"test-{baseId}-{rnd.Next(0, 1000)}";
            var data = GenerateStreamData();

            var streamProvider = ServiceProvider.GetRequiredServiceByName<IStreamProvider>(providerName);
            var stream = streamProvider.GetStream<StreamDataType>(streamId, streamNamespace);

            // subscribe to receive the message.
            var receivedMessages = new List<StreamDataType>();
            var subscriptionHandle = await stream.SubscribeAsync((message, token) =>
            {
                receivedMessages.Add(message);
                return Task.CompletedTask;
            });

            // Create a proper Orleans message using the SAME streamId and streamNamespace as the subscription; but written in previous format
            var azureQueueDataAdapter = new AzureQueueDataAdapterV2(ServiceProvider.GetRequiredService<Orleans.Serialization.SerializationManager>());
            var orleansBatchMessage = azureQueueDataAdapter.ToQueueMessage(streamId, streamNamespace, [ data ], null, new Dictionary<string, object>());

            var queueServiceClient = new Azure.Storage.Queues.QueueServiceClient(TestDefaultConfiguration.DataQueueUri, TestDefaultConfiguration.TokenCredential);
            var queueClient = queueServiceClient.GetQueueClient(MigrationStreamingAzureQueueSetup.QueueName);

            await queueClient.CreateIfNotExistsAsync();
            var response = await queueClient.SendMessageAsync(orleansBatchMessage);
            Assert.Equal(201, response.GetRawResponse().Status);

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
                Version = rnd.Next(0, 500)
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
