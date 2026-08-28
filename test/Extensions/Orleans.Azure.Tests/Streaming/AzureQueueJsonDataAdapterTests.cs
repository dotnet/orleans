#pragma warning disable StreamingJsonSerializationExperimental // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Buffers.Text;
using JsonValueKind = System.Text.Json.JsonValueKind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue.Json;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("AzureStorage"), TestCategory("Streaming")]
    [TestSuite("BVT")]
    [TestProvider("AzureStorage")]
    [TestArea("Streaming")]
    public class AzureQueueJsonDataAdapterTests
    {
        private const string CompactOrleans3JsonMessage =
            "{\"version\":1,\"stream\":{\"namespace\":\"test-namespace\",\"key\":\"00112233445566778899aabbccddeeff\"}," +
            "\"events\":[\"test-event\"],\"requestContext\":{\"key\":\"value\"}}";

        private const string LegacyDirectContainerJsonMessage =
            "{\"$id\":\"1\",\"$type\":\"Orleans.Providers.Streams.AzureQueue.AzureQueueBatchContainerV2, Orleans.Streaming.AzureStorage\"," +
            "\"events\":{\"$type\":\"System.Collections.Generic.List`1[[System.Object, System.Private.CoreLib]], System.Private.CoreLib\"," +
            "\"$values\":[\"test-event\"]},\"requestContext\":{\"$id\":\"2\"," +
            "\"$type\":\"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.Object, System.Private.CoreLib]], System.Private.CoreLib\"," +
            "\"key\":\"value\"},\"StreamId\":{\"$id\":\"3\",\"$type\":\"Orleans.Runtime.StreamId, Orleans.Streaming\"," +
            "\"fk\":{\"$type\":\"System.Byte[], System.Private.CoreLib\"," +
            "\"$value\":\"dGVzdC1uYW1lc3BhY2UwMDExMjIzMzQ0NTU2Njc3ODg5OWFhYmJjY2RkZWVmZg==\"}," +
            "\"ki\":14,\"fh\":1821817189}}";

        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;

        public AzureQueueJsonDataAdapterTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        private AzureQueueJsonDataAdapter InitializeQueueJsonDataAdapter(AzureQueueJsonDataAdapterOptions? options = null)
        {
            var serializer = this.fixture.Services.GetRequiredService<Serializer>();
            var azureQueueDataAdapterV2 = new AzureQueueDataAdapterV2(serializer);
            var jsonOptions = Options.Create(new OrleansJsonSerializerOptions
            {
                JsonSerializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(this.fixture.Services)
            });
            var jsonOrleansSerializer = new OrleansJsonSerializer(jsonOptions);

            return new AzureQueueJsonDataAdapter(
                jsonOrleansSerializer,
                fallbackAdapter: azureQueueDataAdapterV2,
                options ?? new AzureQueueJsonDataAdapterOptions(),
                NullLogger<AzureQueueJsonDataAdapter>.Instance);
        }

        private AzureQueueDataAdapterV2 InitializeBinaryOnlyAdapter()
        {
            var serializer = this.fixture.Services.GetRequiredService<Serializer>();

            var codec = serializer.SessionPool.CodecProvider.TryGetCodec<EventData>();
            Assert.NotNull(codec);
            this.output.WriteLine("Codec for EventData: {0}", codec);

            return new AzureQueueDataAdapterV2(serializer);
        }

        [Fact, TestCategory("BVT")]
        public void ToAndFromQueueMessage_SerializesAccordingToFormat()
        {
            var queueDataAdapter = InitializeQueueJsonDataAdapter();

            var data = new EventData();
            var token = new EventSequenceTokenV2();

            var msg = queueDataAdapter.ToQueueMessage(
                StreamId.Create("ns", Guid.NewGuid()),
                [data],
                token,
                new Dictionary<string, object> { ["context"] = data });

            this.output.WriteLine("Serialized message: {0}", msg);
            Assert.True(IsValidJson(msg), "Message should be valid JSON");
            var envelope = JObject.Parse(msg);
            Assert.Equal(
                ["version", "stream", "events", "requestContext"],
                envelope.Properties().Select(property => property.Name));
            Assert.NotNull(envelope["events"]![0]!["$type"]);
            Assert.NotNull(envelope["requestContext"]!["context"]!["$type"]);
            Assert.DoesNotContain("AzureQueueBatchContainerV2", msg);
            Assert.DoesNotContain("System.Collections.Generic.List", msg);
            Assert.DoesNotContain("System.Collections.Generic.Dictionary", msg);
            Assert.DoesNotContain(nameof(StreamId), msg);
            Assert.DoesNotContain("\"$id\"", msg);

            var batchContainer = queueDataAdapter.FromQueueMessage(msg, token.SequenceNumber);
            var deserializedMsg = batchContainer.GetEvents<EventData>().FirstOrDefault();
            Assert.NotNull(deserializedMsg);
            Assert.Equal(data, deserializedMsg.Item1);
            try
            {
                Assert.True(batchContainer.ImportRequestContext());
                Assert.Equal(data, Assert.IsType<EventData>(RequestContext.Get("context")));
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("BVT")]
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

            Assert.False(IsValidJson(msg), "Binary adapter should not produce JSON");
            Assert.True(IsValidBase64String(msg), "Binary adapter should produce valid base64");

            var batchContainer = binaryAdapter.FromQueueMessage(msg, token.SequenceNumber);
            var deserializedEvent = batchContainer.GetEvents<EventData>().FirstOrDefault();

            Assert.NotNull(deserializedEvent);
            Assert.Equal(data, deserializedEvent.Item1);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_FallsBackToBinaryWhenDeserializingBinaryData()
        {
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

            var jsonAdapter = InitializeQueueJsonDataAdapter();

            var batchContainer = jsonAdapter.FromQueueMessage(binaryMsg, token.SequenceNumber);
            var deserializedEvent = batchContainer.GetEvents<EventData>().FirstOrDefault();

            Assert.NotNull(deserializedEvent);
            Assert.Equal(data, deserializedEvent.Item1);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_DeserializesOrleans7BinaryMessage()
        {
            const string orleans727Message = "IMABIQADSQUPcGF5bG9hZODBASFAGWxlZ2FjeXN0cmVhbQENYTLyfF/g4A==";
            var jsonAdapter = InitializeQueueJsonDataAdapter();

            var batchContainer = jsonAdapter.FromQueueMessage(orleans727Message, sequenceId: 42);
            var deserializedEvent = Assert.Single(batchContainer.GetEvents<string>());

            Assert.Equal("payload", deserializedEvent.Item1);
            Assert.Equal(new EventSequenceTokenV2(42), deserializedEvent.Item2);
            Assert.Equal(StreamId.Create("legacy", "stream"), batchContainer.StreamId);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_DeserializesCompactOrleans3JsonMessage()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();

            var batchContainer = jsonAdapter.FromQueueMessage(CompactOrleans3JsonMessage, sequenceId: 43);
            var deserializedEvent = Assert.Single(batchContainer.GetEvents<string>());

            Assert.Equal("test-event", deserializedEvent.Item1);
            Assert.Equal(new EventSequenceTokenV2(43), deserializedEvent.Item2);
            Assert.Equal(
                StreamId.Create("test-namespace", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
                batchContainer.StreamId);
        }

        [Theory, TestCategory("BVT")]
        [InlineData("\"1\"")]
        [InlineData("2147483648")]
        [InlineData("2")]
        public void JsonAdapter_RejectsUnsupportedCompactEnvelopeVersion(string version)
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter(new AzureQueueJsonDataAdapterOptions { EnableFallback = false });

            var exception = Assert.Throws<InvalidDataException>(
                () => jsonAdapter.FromQueueMessage($"{{\"version\":{version}}}", sequenceId: 0));

            Assert.Contains("Unsupported Azure Queue JSON envelope version", exception.Message);
            Assert.Contains(version, exception.Message);
        }

        [Theory, TestCategory("BVT")]
        [MemberData(nameof(MalformedCompactEnvelopeCases))]
        public void JsonAdapter_RejectsMalformedCompactEnvelope(
            string message,
            string expectedProperty,
            string expectedKind)
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter(new AzureQueueJsonDataAdapterOptions { EnableFallback = false });

            var exception = Assert.Throws<InvalidDataException>(
                () => jsonAdapter.FromQueueMessage(message, sequenceId: 0));

            Assert.Contains($"property '{expectedProperty}'", exception.Message);
            Assert.Contains(expectedKind, exception.Message);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_RejectsInvalidJson()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter(new AzureQueueJsonDataAdapterOptions { EnableFallback = false });

            Assert.ThrowsAny<System.Text.Json.JsonException>(
                () => jsonAdapter.FromQueueMessage("{\"version\":1", sequenceId: 0));
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_ProducesCompactVersion1Message()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();

            var message = jsonAdapter.ToQueueMessage(
                StreamId.Create("test-namespace", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
                ["test-event"],
                token: null,
                new Dictionary<string, object> { ["key"] = "value" });

            Assert.Equal(CompactOrleans3JsonMessage, message);
            Assert.DoesNotContain("$type", message);
            Assert.DoesNotContain("$id", message);
            Assert.DoesNotContain("StreamId", message);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_RoundTripsNamespaceLessGuidStream()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();
            var streamId = StreamId.Create(null, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

            var message = jsonAdapter.ToQueueMessage(streamId, ["test-event"], token: null, requestContext: null);
            var batchContainer = jsonAdapter.FromQueueMessage(message, sequenceId: 45);

            Assert.Contains("\"namespace\":null", message);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [Theory, TestCategory("BVT")]
        [InlineData("customer/\u03B2")]
        [InlineData("00112233-4455-6677-8899-aabbccddeeff")]
        public void JsonAdapter_RoundTripsUtf8StringStreamKey(string key)
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();
            var streamId = StreamId.Create("string-key", key);

            var message = jsonAdapter.ToQueueMessage(streamId, ["test-event"], token: null, requestContext: null);
            var batchContainer = jsonAdapter.FromQueueMessage(message, sequenceId: 48);

            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_RoundTripsSharedReferencesWithinCollections()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();
            var sharedEvent = new EventData { Id = 123, Name = "shared" };
            var sharedContext = new EventData { Id = 456, Name = "context" };

            var message = jsonAdapter.ToQueueMessage(
                StreamId.Create("shared", Guid.NewGuid()),
                [sharedEvent, sharedEvent],
                token: null,
                new Dictionary<string, object>
                {
                    ["first"] = sharedContext,
                    ["second"] = sharedContext
                });
            Assert.Contains("\"$id\"", message);
            Assert.Contains("\"$ref\"", message);
            var batchContainer = jsonAdapter.FromQueueMessage(message, sequenceId: 46);
            var deserializedEvents = batchContainer.GetEvents<EventData>().Select(item => item.Item1).ToList();

            Assert.Same(deserializedEvents[0], deserializedEvents[1]);
            try
            {
                Assert.True(batchContainer.ImportRequestContext());
                Assert.Same(RequestContext.Get("first"), RequestContext.Get("second"));
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_SerializesNullRequestContextAsEmptyObject()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();

            var message = jsonAdapter.ToQueueMessage(
                StreamId.Create("context", Guid.NewGuid()),
                ["test-event"],
                token: null,
                requestContext: null);
            var batchContainer = jsonAdapter.FromQueueMessage(message, sequenceId: 47);

            Assert.EndsWith("\"requestContext\":{}}", message);
            Assert.True(batchContainer.ImportRequestContext());
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_DeserializesLegacyDirectContainerJsonMessage()
        {
            var jsonAdapter = InitializeQueueJsonDataAdapter();

            var batchContainer = jsonAdapter.FromQueueMessage(LegacyDirectContainerJsonMessage, sequenceId: 44);
            var deserializedEvent = Assert.Single(batchContainer.GetEvents<string>());

            Assert.Equal("test-event", deserializedEvent.Item1);
            Assert.Equal(new EventSequenceTokenV2(44), deserializedEvent.Item2);
            Assert.Equal(
                StreamId.Create("test-namespace", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
                batchContainer.StreamId);
        }

        [Fact, TestCategory("BVT")]
        public void BinaryPreferredAdapter_FallsBackToJsonWhenDeserializingJsonData()
        {
            var jsonFirstAdapter = InitializeQueueJsonDataAdapter();
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

            var binaryPreferredAdapter = InitializeQueueJsonDataAdapter(new AzureQueueJsonDataAdapterOptions { PreferJson = false });

            var batchContainer = binaryPreferredAdapter.FromQueueMessage(jsonMsg, token.SequenceNumber);
            var deserializedEvent = batchContainer.GetEvents<EventData>().FirstOrDefault();

            Assert.NotNull(deserializedEvent);
            Assert.Equal(data, deserializedEvent.Item1);
            Assert.Equal(streamId, batchContainer.StreamId);
        }

        [Fact, TestCategory("BVT")]
        public void JsonAdapter_WithoutFallback_FailsOnIncompatibleData()
        {
            var binaryAdapter = InitializeBinaryOnlyAdapter();
            var data = new EventData { Id = 999, Name = "FailureTest" };
            var token = new EventSequenceTokenV2();

            var binaryMsg = binaryAdapter.ToQueueMessage(
                StreamId.Create("failure-ns", Guid.NewGuid()),
                [data],
                token,
                new Dictionary<string, object>());

            var jsonAdapterNoFallback = InitializeQueueJsonDataAdapter(new AzureQueueJsonDataAdapterOptions { EnableFallback = false });

            Assert.ThrowsAny<Exception>(() => jsonAdapterNoFallback.FromQueueMessage(binaryMsg, token.SequenceNumber));
        }

        [Fact, TestCategory("BVT")]
        public void Configurators_UseProviderSpecificAdapterOptions()
        {
            const string binaryProviderName = "binary-preferred";
            const string jsonProviderName = "json-preferred";
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(this.fixture.Services.GetRequiredService<Serializer>());
            services.AddSingleton(this.fixture.Services.GetRequiredService<IRuntimeClient>());

            var binaryConfigurator = new SiloAzureQueueJsonStreamConfigurator(binaryProviderName, configure => configure(services));
            binaryConfigurator.ConfigureJsonAdapter(options => options.PreferJson = false);
            _ = new SiloAzureQueueJsonStreamConfigurator(jsonProviderName, configure => configure(services));

            using var serviceProvider = services.BuildServiceProvider();
            var binaryAdapter = serviceProvider.GetRequiredKeyedService<IQueueDataAdapter<string, IBatchContainer>>(binaryProviderName);
            var jsonAdapter = serviceProvider.GetRequiredKeyedService<IQueueDataAdapter<string, IBatchContainer>>(jsonProviderName);
            var streamId = StreamId.Create("options", Guid.NewGuid());
            var events = new[] { new EventData { Id = 1, Name = "test" } };

            var binaryMessage = binaryAdapter.ToQueueMessage(streamId, events, token: null, requestContext: null);
            var jsonMessage = jsonAdapter.ToQueueMessage(streamId, events, token: null, requestContext: null);

            Assert.False(IsValidJson(binaryMessage));
            Assert.True(IsValidJson(jsonMessage));
        }

        public static TheoryData<string, string, string> MalformedCompactEnvelopeCases => new()
        {
            {
                """{"version":1,"events":[],"requestContext":{}}""",
                "stream",
                nameof(JsonValueKind.Object)
            },
            {
                """{"version":1,"stream":[],"events":[],"requestContext":{}}""",
                "stream",
                nameof(JsonValueKind.Object)
            },
            {
                """{"version":1,"stream":{"key":"key"},"events":[],"requestContext":{}}""",
                "namespace",
                $"{nameof(JsonValueKind.String)} or {nameof(JsonValueKind.Null)}"
            },
            {
                """{"version":1,"stream":{"namespace":1,"key":"key"},"events":[],"requestContext":{}}""",
                "namespace",
                $"{nameof(JsonValueKind.String)} or {nameof(JsonValueKind.Null)}"
            },
            {
                """{"version":1,"stream":{"namespace":"namespace"},"events":[],"requestContext":{}}""",
                "key",
                nameof(JsonValueKind.String)
            },
            {
                """{"version":1,"stream":{"namespace":"namespace","key":null},"events":[],"requestContext":{}}""",
                "key",
                nameof(JsonValueKind.String)
            },
            {
                """{"version":1,"stream":{"namespace":"namespace","key":"key"},"requestContext":{}}""",
                "events",
                nameof(JsonValueKind.Array)
            },
            {
                """{"version":1,"stream":{"namespace":"namespace","key":"key"},"events":{},"requestContext":{}}""",
                "events",
                nameof(JsonValueKind.Array)
            },
            {
                """{"version":1,"stream":{"namespace":"namespace","key":"key"},"events":[]}""",
                "requestContext",
                nameof(JsonValueKind.Object)
            },
            {
                """{"version":1,"stream":{"namespace":"namespace","key":"key"},"events":[],"requestContext":[]}""",
                "requestContext",
                nameof(JsonValueKind.Object)
            }
        };

        [GenerateSerializer]
        public sealed class EventData : IEquatable<EventData>
        {
            [Id(0)]
            public int Id { get; set; }

            [Id(1)]
            public string Name { get; set; } = string.Empty;

            public override bool Equals(object? obj) => Equals(obj as EventData);
            public bool Equals(EventData? other) => other is not null && Id == other.Id && Name == other.Name;
            public override int GetHashCode() => HashCode.Combine(Id, Name);

            public static bool operator ==(EventData? left, EventData? right) => EqualityComparer<EventData>.Default.Equals(left, right);
            public static bool operator !=(EventData? left, EventData? right) => !(left == right);
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
            return !string.IsNullOrWhiteSpace(s) && Base64.IsValid(s, out _);
        }
    }
}
#pragma warning restore StreamingJsonSerializationExperimental // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
