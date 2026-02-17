#pragma warning disable StreamingJsonSerializationExperimental // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue.Json;
using Orleans.Streams;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("AzureStorage"), TestCategory("Streaming")]
    public class AzureQueueJsonDataAdapterTests : AzureStorageBasicTests, IAsyncLifetime
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private const int NumBatches = 20;
        private const int NumMessagesPerBatch = 20;
        public static readonly string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AQAdapterTests";
        private readonly ILoggerFactory loggerFactory;
        private static readonly List<string> azureQueueNames = AzureQueueUtilities.GenerateQueueNames($"AzureQueueAdapterTests-{Guid.NewGuid()}", 8);

        public AzureQueueJsonDataAdapterTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.loggerFactory = this.fixture.Services.GetService<ILoggerFactory>();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            try
            {
                TestUtils.CheckForAzureStorage();
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(this.loggerFactory, azureQueueNames, new AzureQueueOptions().ConfigureTestDefaults());
            }
            catch (SkipException) { }
        }

        private AzureQueueJsonDataAdapter InitializeQueueJsonDataAdapter(bool enableFallback, bool preferJson)
        {
            var serializer = this.fixture.Services.GetService<Serializer>();
            var azureQueueDataAdapterV2 = new AzureQueueDataAdapterV2(serializer);
            var jsonOrleansSerializer = new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()));
            var logger = Substitute.For<ILogger<AzureQueueJsonDataAdapter>>();

            var jsonQueueDataAdapter = new AzureQueueJsonDataAdapter(
                jsonOrleansSerializer,
                fallbackAdapter: azureQueueDataAdapterV2,
                new AzureQueueJsonDataAdapterOptions() { EnableFallback = enableFallback, PreferJson = preferJson },
                logger);

            return jsonQueueDataAdapter;
        }

        private AzureQueueDataAdapterV2 InitializeBinaryOnlyAdapter()
        {
            var serializer = this.fixture.Services.GetService<Serializer>();

            var codec = serializer.SessionPool.CodecProvider.TryGetCodec<EventData>();
            Assert.NotNull(codec);
            this.output.WriteLine("Codec for EventData: {0}", codec);

            return new AzureQueueDataAdapterV2(serializer);
        }

        [SkippableFact, TestCategory("Functional")]
        public void ToAndFromQueueMessage_SerializesAccordingToFormat()
        {
            var options = new AzureQueueOptions
            {
                MessageVisibilityTimeout = TimeSpan.FromSeconds(30),
                QueueNames = azureQueueNames
            };
            options.ConfigureTestDefaults();
            var queueCacheOptions = new SimpleQueueCacheOptions();
            var queueDataAdapter = InitializeQueueJsonDataAdapter(enableFallback: true, preferJson: true);

            var data = new EventData();
            var token = new EventSequenceTokenV2();

            var msg = queueDataAdapter.ToQueueMessage(
                StreamId.Create("ns", Guid.NewGuid()),
                [data],
                token,
                new Dictionary<string, object>());

            this.output.WriteLine("Serialized message: {0}", msg);
            Assert.True(IsValidJson(msg), "Message should be valid JSON");

            var batchContainer = queueDataAdapter.FromQueueMessage(msg, token.SequenceNumber);
            var deserializedMsg = batchContainer.GetEvents<EventData>().FirstOrDefault();
            Assert.NotNull(deserializedMsg);
            Assert.Equal(data, deserializedMsg.Item1);
        }

        [SkippableFact, TestCategory("Functional")]
        public void BinaryOnlyAdapter_SerializesToBinaryFormat()
        {
            var binaryAdapter = InitializeBinaryOnlyAdapter();
            var data = new EventData { Id = 123, Name = "BinaryTest" };
            var token = new EventSequenceTokenV2();
            var streamId = StreamId.Create("binary-ns", Guid.NewGuid());

            var msg = binaryAdapter.ToQueueMessage(
                streamId,
                [data],
                token,
                new Dictionary<string, object> { { "source", "binary-test" } });

            this.output.WriteLine("Binary serialized message: {0}", msg);
            
            // Should be base64 encoded binary data, not JSON
            Assert.False(IsValidJson(msg), "Binary adapter should not produce JSON");
            Assert.True(IsValidBase64String(msg), "Binary adapter should produce valid base64");

            // Verify round-trip works
            var batchContainer = binaryAdapter.FromQueueMessage(msg, token.SequenceNumber);
            var deserializedEvent = batchContainer.GetEvents<EventData>().FirstOrDefault();
            
            Assert.NotNull(deserializedEvent);
            Assert.Equal(data, deserializedEvent.Item1);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [SkippableFact, TestCategory("Functional")]
        public void JsonAdapter_FallsBackToBinaryWhenDeserializingBinaryData()
        {
            // First create a binary message using the V2 adapter
            var binaryAdapter = InitializeBinaryOnlyAdapter();
            var data = new EventData { Id = 456, Name = "FallbackTest" };
            var token = new EventSequenceTokenV2();
            var streamId = StreamId.Create("fallback-ns", Guid.NewGuid());

            var binaryMsg = binaryAdapter.ToQueueMessage(
                streamId,
                [data],
                token,
                new Dictionary<string, object> { { "format", "binary" } });

            this.output.WriteLine("Original binary message: {0}", binaryMsg);
            Assert.True(IsValidBase64String(binaryMsg), "Should be valid base64 binary data");

            // Now try to deserialize it with JSON adapter
            var jsonAdapter = InitializeQueueJsonDataAdapter(enableFallback: true, preferJson: true);
            
            var batchContainer = jsonAdapter.FromQueueMessage(binaryMsg, token.SequenceNumber);
            var deserializedEvent = batchContainer.GetEvents<EventData>().FirstOrDefault();
            
            Assert.NotNull(deserializedEvent);
            Assert.Equal(data, deserializedEvent.Item1);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [SkippableFact, TestCategory("Functional")]
        public void BinaryPreferredAdapter_FallsBackToJsonWhenDeserializingJsonData()
        {
            // First create a JSON message using JSON-preferred adapter
            var jsonFirstAdapter = InitializeQueueJsonDataAdapter(enableFallback: true, preferJson: true);
            var data = new EventData { Id = 789, Name = "JsonToJsonTest" };
            var token = new EventSequenceTokenV2();
            var streamId = StreamId.Create("json-fallback-ns", Guid.NewGuid());

            var jsonMsg = jsonFirstAdapter.ToQueueMessage(
                streamId,
                [data],
                token,
                new Dictionary<string, object> { { "format", "json" } });

            this.output.WriteLine("Original JSON message: {0}", jsonMsg);
            Assert.True(IsValidJson(jsonMsg), "Should be valid JSON data");

            // Now try to deserialize it with binary-preferred adapter
            var binaryPreferredAdapter = InitializeQueueJsonDataAdapter(enableFallback: true, preferJson: false);
            
            var batchContainer = binaryPreferredAdapter.FromQueueMessage(jsonMsg, token.SequenceNumber);
            var deserializedEvent = batchContainer.GetEvents<EventData>().FirstOrDefault();
            
            Assert.NotNull(deserializedEvent);
            Assert.Equal(data, deserializedEvent.Item1);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [SkippableFact, TestCategory("Functional")]
        public void JsonAdapter_WithoutFallback_FailsOnIncompatibleData()
        {
            // Create a binary message
            var binaryAdapter = InitializeBinaryOnlyAdapter();
            var data = new EventData { Id = 999, Name = "FailureTest" };
            var token = new EventSequenceTokenV2();

            var binaryMsg = binaryAdapter.ToQueueMessage(
                StreamId.Create("failure-ns", Guid.NewGuid()),
                [data],
                token,
                new Dictionary<string, object>());

            // Try to deserialize with JSON adapter that has fallback disabled
            var jsonAdapterNoFallback = InitializeQueueJsonDataAdapter(enableFallback: false, preferJson: true);
            
            Assert.ThrowsAny<Exception>(() => jsonAdapterNoFallback.FromQueueMessage(binaryMsg, token.SequenceNumber));
        }

        [GenerateSerializer]
        public class EventData : IEquatable<EventData>
        {
            [Id(0)]
            public int Id { get; set; }
            
            [Id(1)]
            public string Name { get; set; }

            public override bool Equals(object obj) => Equals(obj as EventData);
            public bool Equals(EventData other) => other is not null && Id == other.Id && Name == other.Name;
            public override int GetHashCode() => HashCode.Combine(Id, Name);

            public static bool operator ==(EventData left, EventData right) => EqualityComparer<EventData>.Default.Equals(left, right);
            public static bool operator !=(EventData left, EventData right) => !(left == right);
        }

        private static bool IsValidJson(string msg)
        {
            try
            {
                _ = JsonConvert.DeserializeObject(msg);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsValidBase64String(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            // generated by copilot
            return Base64.IsValid(s);
        }
    }
}
#pragma warning restore StreamingJsonSerializationExperimental // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
