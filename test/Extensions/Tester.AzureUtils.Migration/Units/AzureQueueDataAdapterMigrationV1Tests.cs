using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        public void ToQueueMessage_WithJsonMode_ProducesJsonString()
        {
            var streamGuid = Guid.NewGuid();
            var streamNamespace = "test-namespace";
            var events = new[] { new TestEvent { Id = 123, Message = "test" } };
            var requestContext = new Dictionary<string, object> { { "key", "value" } };

            var result = adapter.ToQueueMessage(streamGuid, streamNamespace, events, null, requestContext);

            Assert.NotNull(result);
            Assert.NotEmpty(result);
            // JSON should contain readable text, not base64
            Assert.Contains("\"Id\":", result);
            Assert.Contains("123", result);
            Assert.Contains("\"Message\":", result);
            Assert.Contains("test", result);
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
    }

    [Serializable]
    public class TestEvent
    {
        public int Id { get; set; }
        public string? Message { get; set; } = default!;
    }
}