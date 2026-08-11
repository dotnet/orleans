using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.AzureQueue.Migration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.Migration.Configuration;
using Orleans.Streams;
using Xunit;

namespace Tester.AzureUtils.Migration.Units
{
    public class AzureQueueDataAdapterMigrationV1Tests
    {
        private static readonly Guid Version10FixtureStreamGuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        private const string Version10GoldenJson =
            "{\"$id\":\"1\",\"$type\":\"Orleans.Providers.Streams.AzureQueue.AzureQueueBatchContainerV2, Orleans.Streaming.AzureStorage\"," +
            "\"events\":{\"$type\":\"System.Collections.Generic.List`1[[System.Object, System.Private.CoreLib]], System.Private.CoreLib\"," +
            "\"$values\":[\"test-event\"]},\"requestContext\":{\"$id\":\"2\"," +
            "\"$type\":\"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.Object, System.Private.CoreLib]], System.Private.CoreLib\"," +
            "\"key\":\"value\"},\"StreamId\":{\"$id\":\"3\",\"$type\":\"Orleans.Runtime.StreamId, Orleans.Streaming\"," +
            "\"fk\":{\"$type\":\"System.Byte[], System.Private.CoreLib\"," +
            "\"$value\":\"dGVzdC1uYW1lc3BhY2UwMDExMjIzMzQ0NTU2Njc3ODg5OWFhYmJjY2RkZWVmZg==\"}," +
            "\"ki\":14,\"fh\":1821817189}}";

        private readonly AzureQueueDataAdapterMigrationV1 adapter;
        private readonly SerializationManager serializationManager;
        private readonly OrleansMigrationJsonSerializer jsonSerializer;

        public AzureQueueDataAdapterMigrationV1Tests()
        {
            var silo = new Microsoft.Extensions.Hosting.HostBuilder()
                .UseOrleans((Microsoft.Extensions.Hosting.HostBuilderContext ctx, ISiloBuilder siloBuilder) =>
                {
                    siloBuilder
                        .Configure<ClusterOptions>(o => o.ClusterId = o.ServiceId = "test")
                        .AddMigrationTools()
                        .UseLocalhostClustering();
                })
                .Build();

            this.serializationManager = silo.Services.GetRequiredService<SerializationManager>();
            this.jsonSerializer = silo.Services.GetRequiredService<OrleansMigrationJsonSerializer>();

            var logger = silo.Services.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.Json,
                DeserializationMode = DeserializationMode.PreferJson
            };

            BufferPool.InitGlobalBufferPool(new SiloMessagingOptions());

            this.adapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);
        }

        [Fact]
        public void ToQueueMessage_WithJsonMode_ProducesVersion10GoldenJson()
        {
            var streamNamespace = "test-namespace";
            var events = new[] { "test-event" };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var result = adapter.ToQueueMessage(Version10FixtureStreamGuid, streamNamespace, events, null, requestContext);

            Assert.Equal(Version10GoldenJson, result);
            Assert.DoesNotContain("\"StreamGuid\"", result);
            Assert.DoesNotContain("\"StreamNamespace\"", result);
        }

        [Fact]
        public void ToQueueMessage_WithJsonMode_ProducesVersion10ConsumableStreamIdFixture()
        {
            var result = adapter.ToQueueMessage(
                Version10FixtureStreamGuid,
                "test-namespace",
                new[] { "test-event" },
                null,
                new Dictionary<string, object> { { "key", "value" } });

            var fixture = Assert.IsType<Version10BatchContainerWireFixture>(
                JsonConvert.DeserializeObject<Version10BatchContainerWireFixture>(
                    result,
                    new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore }));

            var streamId = Assert.IsType<Version10StreamIdWireFixture>(fixture.StreamId);
            var fullKey = Convert.FromBase64String(streamId.FullKey.Value);
            Assert.Equal("test-namespace00112233445566778899aabbccddeeff", Encoding.UTF8.GetString(fullKey));
            Assert.Equal(14, streamId.KeyIndex);
            Assert.Equal(1821817189, streamId.Hash);
        }

        [Fact]
        public void ToQueueMessage_WithBinaryMode_ProducesBase64String()
        {
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.Binary,
                DeserializationMode = DeserializationMode.PreferBinary
            };
            var logger = this.serializationManager.ServiceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var binaryAdapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);

            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 123, Message = "test" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var result = binaryAdapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);

            Assert.NotNull(result);
            Assert.NotEmpty(result);
            // Should be valid base64
            Assert.True(IsValidBase64(result));
        }

        [Fact]
        public void ToQueueMessage_WithJsonWithFallbackMode_ProducesJsonString()
        {
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.JsonWithFallback,
                DeserializationMode = DeserializationMode.PreferJson
            };
            var logger = this.serializationManager.ServiceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var fallbackAdapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);

            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 456, Message = "fallback" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var result = fallbackAdapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);

            Assert.NotNull(result);
            Assert.NotEmpty(result);
            // JSON should contain readable text
            Assert.Contains("\"Id\":", result);
            Assert.Contains("456", result);
            Assert.Contains("\"Message\":", result);
            Assert.Contains("fallback", result);
        }

        [Fact]
        public void FromQueueMessage_WithJsonMessage_DeserializesCorrectly()
        {
            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 789, Message = "json-test" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var queueMessage = adapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);
            var sequenceId = 12345L;

            var result = adapter.FromQueueMessage(queueMessage, sequenceId);

            Assert.NotNull(result);
            Assert.Equal(streamGuid, result.StreamGuid);
            Assert.Equal(streamNamespace, result.StreamNamespace);

            var deserializedEvents = result.GetEvents<TestEvent>().ToList();
            Assert.Single(deserializedEvents);
            Assert.Equal(789, deserializedEvents[0].Item1.Id);
            Assert.Equal("json-test", deserializedEvents[0].Item1.Message);
            Assert.Equal(sequenceId, deserializedEvents[0].Item2.SequenceNumber);
        }

        [Fact]
        public void FromQueueMessage_WithBinaryMessage_DeserializesCorrectly()
        {
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.Binary,
                DeserializationMode = DeserializationMode.PreferBinary
            };
            var logger = this.serializationManager.ServiceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var binaryAdapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);

            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 999, Message = "binary-test" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var queueMessage = binaryAdapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);
            var sequenceId = 67890L;

            var result = binaryAdapter.FromQueueMessage(queueMessage, sequenceId);

            Assert.NotNull(result);
            Assert.Equal(streamGuid, result.StreamGuid);
            Assert.Equal(streamNamespace, result.StreamNamespace);

            var deserializedEvents = result.GetEvents<TestEvent>().ToList();
            Assert.Single(deserializedEvents);
            Assert.Equal(999, deserializedEvents[0].Item1.Id);
            Assert.Equal("binary-test", deserializedEvents[0].Item1.Message);
            Assert.Equal(sequenceId, deserializedEvents[0].Item2.SequenceNumber);
        }

        [Fact]
        public void FromQueueMessage_WithPreferJsonMode_FallsBackToBinary()
        {
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.Binary,
                DeserializationMode = DeserializationMode.PreferBinary
            };
            var logger = this.serializationManager.ServiceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var binaryAdapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);

            var streamGuid = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
            var binaryMessage = binaryAdapter.ToQueueMessage(
                streamGuid,
                "binary-fallback",
                new[] { new TestEvent { Id = 321, Message = "legacy-binary" } },
                null,
                new Dictionary<string, object> { { "format", "binary" } });

            var result = adapter.FromQueueMessage(binaryMessage, 24680L);
            var deserializedEvent = Assert.Single(result.GetEvents<TestEvent>());

            Assert.Equal(streamGuid, result.StreamGuid);
            Assert.Equal("binary-fallback", result.StreamNamespace);
            Assert.Equal(321, deserializedEvent.Item1.Id);
            Assert.Equal("legacy-binary", deserializedEvent.Item1.Message);
            Assert.Equal(24680L, deserializedEvent.Item2.SequenceNumber);
        }

        [Fact]
        public void FromQueueMessage_WithPreferJsonMode_TriesJsonFirst()
        {
            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 111, Message = "prefer-json" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var jsonMessage = adapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);
            var sequenceId = 11111L;

            var result = adapter.FromQueueMessage(jsonMessage, sequenceId);

            Assert.NotNull(result);
            var deserializedEvents = result.GetEvents<TestEvent>().ToList();
            Assert.Single(deserializedEvents);
            Assert.Equal(111, deserializedEvents[0].Item1.Id);
            Assert.Equal("prefer-json", deserializedEvents[0].Item1.Message);
        }

        [Fact]
        public void FromQueueMessage_WithPreferBinaryMode_TriesBinaryFirst()
        {
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.Binary,
                DeserializationMode = DeserializationMode.PreferBinary
            };
            var logger = this.serializationManager.ServiceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var binaryAdapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);

            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 222, Message = "prefer-binary" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var binaryMessage = binaryAdapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);
            var sequenceId = 22222L;

            var result = binaryAdapter.FromQueueMessage(binaryMessage, sequenceId);

            Assert.NotNull(result);
            var deserializedEvents = result.GetEvents<TestEvent>().ToList();
            Assert.Single(deserializedEvents);
            Assert.Equal(222, deserializedEvents[0].Item1.Id);
            Assert.Equal("prefer-binary", deserializedEvents[0].Item1.Message);
        }

        [Fact]
        public void RoundTrip_JsonSerialization_PreservesEventData()
        {
            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var originalEvents = new[]
            {
                new TestEvent { Id = 1, Message = "first" },
                new TestEvent { Id = 2, Message = "second" }
            };
            var requestContext = new Dictionary<string, object>
            {
                { "correlation-id", "12345" },
                { "user-id", "test-user" }
            };

            var queueMessage = adapter.ToQueueMessage(streamGuid, streamNamespace, originalEvents, null, requestContext);
            var batchContainer = adapter.FromQueueMessage(queueMessage, 98765L);

            Assert.Equal(streamGuid, batchContainer.StreamGuid);
            Assert.Equal(streamNamespace, batchContainer.StreamNamespace);

            var events = batchContainer.GetEvents<TestEvent>().ToList();
            Assert.Equal(2, events.Count);

            Assert.Equal(1, events[0].Item1.Id);
            Assert.Equal("first", events[0].Item1.Message);
            Assert.Equal(98765L, events[0].Item2.SequenceNumber);

            Assert.Equal(2, events[1].Item1.Id);
            Assert.Equal("second", events[1].Item1.Message);
            Assert.Equal(98765L, events[1].Item2.SequenceNumber);
        }

        [Fact]
        public void RoundTrip_JsonSerialization_PreservesDateLikeStringsAndHighPrecisionNumbers()
        {
            var originalEvent = new PrecisionEvent
            {
                DateLikeString = "2020-01-01T00:00:00+05:00",
                HighPrecisionNumber = 1234567890.123456789m,
                LargeNumber = 1e300
            };

            var queueMessage = adapter.ToQueueMessage(
                Guid.NewGuid(),
                "test-namespace",
                new[] { originalEvent },
                null,
                new Dictionary<string, object>());
            var batchContainer = adapter.FromQueueMessage(queueMessage, 123L);
            var deserializedEvent = Assert.Single(batchContainer.GetEvents<PrecisionEvent>()).Item1;

            Assert.Equal(originalEvent.DateLikeString, deserializedEvent.DateLikeString);
            Assert.Equal(originalEvent.HighPrecisionNumber, deserializedEvent.HighPrecisionNumber);
            Assert.Equal(originalEvent.LargeNumber, deserializedEvent.LargeNumber);
        }

        [Fact]
        public void RoundTrip_BinarySerialization_PreservesEventData()
        {
            var options = new AzureQueueMigrationOptions
            {
                SerializationMode = SerializationMode.Binary,
                DeserializationMode = DeserializationMode.PreferBinary
            };
            var logger = this.serializationManager.ServiceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
            var binaryAdapter = new AzureQueueDataAdapterMigrationV1(
                logger,
                this.serializationManager,
                this.jsonSerializer,
                options);

            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var originalEvents = new[]
            {
                new TestEvent { Id = 10, Message = "binary-first" },
                new TestEvent { Id = 20, Message = "binary-second" }
            };
            var requestContext = new Dictionary<string, object>
            {
                { "binary-key", "binary-value" }
            };

            var queueMessage = binaryAdapter.ToQueueMessage(streamGuid, streamNamespace, originalEvents, null, requestContext);
            var batchContainer = binaryAdapter.FromQueueMessage(queueMessage, 55555L);

            Assert.Equal(streamGuid, batchContainer.StreamGuid);
            Assert.Equal(streamNamespace, batchContainer.StreamNamespace);

            var events = batchContainer.GetEvents<TestEvent>().ToList();
            Assert.Equal(2, events.Count);

            Assert.Equal(10, events[0].Item1.Id);
            Assert.Equal("binary-first", events[0].Item1.Message);
            Assert.Equal(55555L, events[0].Item2.SequenceNumber);

            Assert.Equal(20, events[1].Item1.Id);
            Assert.Equal("binary-second", events[1].Item1.Message);
            Assert.Equal(55555L, events[1].Item2.SequenceNumber);
        }

        private static bool IsValidBase64(string base64String)
        {
            try
            {
                Convert.FromBase64String(base64String);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class Version10BatchContainerWireFixture
        {
            public Version10StreamIdWireFixture StreamId { get; set; } = null!;
        }

        private sealed class Version10StreamIdWireFixture
        {
            [JsonProperty("fk")]
            public Version10ByteArrayWireFixture FullKey { get; set; } = null!;

            [JsonProperty("ki")]
            public ushort KeyIndex { get; set; }

            [JsonProperty("fh")]
            public int Hash { get; set; }
        }

        private sealed class Version10ByteArrayWireFixture
        {
            [JsonProperty("$value")]
            public string Value { get; set; } = null!;
        }
    }

    [Serializable]
    public class TestEvent
    {
        public int Id { get; set; }
        public string? Message { get; set; } = default!;
    }

    [Serializable]
    public class PrecisionEvent
    {
        public string? DateLikeString { get; set; }
        public decimal HighPrecisionNumber { get; set; }
        public double LargeNumber { get; set; }
    }
}