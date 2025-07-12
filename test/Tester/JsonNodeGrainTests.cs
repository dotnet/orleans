using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Grain interface for testing JsonNode serialization through Orleans grain calls.
    /// This interface reproduces the scenario from GitHub issue #9568 where JsonNode parameters
    /// are used in grain methods, which can receive JsonValue instances at runtime.
    /// </summary>
    public interface IJsonNodeTestGrain : IGrainWithIntegerKey
    {
        Task<JsonNode> ProcessJsonNode(JsonNode node);
        Task<string> GetJsonString(JsonNode node);
    }

    /// <summary>
    /// Implementation of the JsonNode test grain.
    /// Provides methods to test serialization and deserialization of JsonNode instances
    /// through grain method calls, ensuring the fix for issue #9568 works in a distributed context.
    /// </summary>
    public class JsonNodeTestGrain : Grain, IJsonNodeTestGrain
    {
        public Task<JsonNode> ProcessJsonNode(JsonNode node)
        {
            // Simply return the node - serialization should handle it
            return Task.FromResult(node);
        }

        public Task<string> GetJsonString(JsonNode node)
        {
            // Convert to string to verify we received the correct data
            return Task.FromResult(node?.ToJsonString() ?? "null");
        }
    }

    /// <summary>
    /// Test fixture that configures an in-process Orleans cluster with JSON serialization support.
    /// Sets up both silo and client to use the JsonCodec for System.Text.Json types.
    /// </summary>
    public class JsonNodeGrainTestFixture : BaseInProcessTestClusterFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);

            // Configure JSON serialization
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.Services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddJsonSerializer(_ => false);
                });
            });

            builder.ConfigureClient(clientBuilder =>
            {
                clientBuilder.Services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddJsonSerializer(_ => false);
                });
            });
        }
    }

    /// <summary>
    /// Integration tests for JsonNode serialization in Orleans grains.
    /// These tests verify that the fix for GitHub issue #9568 works correctly when JsonNode types
    /// are passed through grain method calls, particularly when JsonValue is passed to a JsonNode parameter.
    /// </summary>
    [Trait("Category", "BVT")]
    public class JsonNodeGrainTests : IClassFixture<JsonNodeGrainTestFixture>
    {
        private readonly JsonNodeGrainTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public JsonNodeGrainTests(JsonNodeGrainTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>
        /// Reproduces GitHub issue #9568: Tests that a grain method with JsonNode parameter can receive JsonValue.
        /// This test verifies the core fix where JsonValue (a derived type) is passed to a method expecting JsonNode (base type).
        /// Previously this would throw InvalidCastException during deserialization.
        /// </summary>
        [Fact]
        public async Task GrainCanProcessJsonValue_AsJsonNode()
        {
            // When a grain method has a JsonNode parameter but receives a JsonValue
            var grain = _fixture.GrainFactory.GetGrain<IJsonNodeTestGrain>(1);

            // Create a JsonValue (which is a subtype of JsonNode)
            JsonNode jsonNode = JsonValue.Create(1);

            // This should not throw InvalidCastException
            var result = await grain.ProcessJsonNode(jsonNode);

            // Verify the result
            Assert.NotNull(result);
            Assert.Equal(1, result.GetValue<int>());
        }

        /// <summary>
        /// Tests that grains can process all JsonNode subtypes (JsonValue, JsonArray, JsonObject).
        /// Verifies that each type maintains its structure and values through grain method calls.
        /// </summary>
        [Fact]
        public async Task GrainCanProcessVariousJsonNodeTypes()
        {
            var grain = _fixture.GrainFactory.GetGrain<IJsonNodeTestGrain>(2);

            // Test JsonValue with int
            JsonNode intValue = JsonValue.Create(42);
            var intResult = await grain.ProcessJsonNode(intValue);
            Assert.Equal(42, intResult.GetValue<int>());

            // Test JsonValue with string
            JsonNode stringValue = JsonValue.Create("hello");
            var stringResult = await grain.ProcessJsonNode(stringValue);
            Assert.Equal("hello", stringResult.GetValue<string>());

            // Test JsonValue with bool
            JsonNode boolValue = JsonValue.Create(true);
            var boolResult = await grain.ProcessJsonNode(boolValue);
            Assert.True(boolResult.GetValue<bool>());

            // Test JsonArray
            JsonNode arrayValue = new JsonArray { 1, 2, 3 };
            var arrayResult = await grain.ProcessJsonNode(arrayValue);
            Assert.IsType<JsonArray>(arrayResult);
            Assert.Equal(3, ((JsonArray)arrayResult).Count);

            // Test JsonObject
            JsonNode objectValue = new JsonObject { ["key"] = "value", ["number"] = 123 };
            var objectResult = await grain.ProcessJsonNode(objectValue);
            Assert.IsType<JsonObject>(objectResult);
            Assert.Equal("value", objectResult["key"]!.GetValue<string>());
            Assert.Equal(123, objectResult["number"]!.GetValue<int>());
        }

        /// <summary>
        /// Tests grain processing of complex nested JSON structures.
        /// Verifies that deeply nested objects containing mixed JsonNode types are correctly
        /// serialized and deserialized through grain calls.
        /// </summary>
        [Fact]
        public async Task GrainCanProcessComplexJsonStructure()
        {
            var grain = _fixture.GrainFactory.GetGrain<IJsonNodeTestGrain>(3);

            // Create a complex nested structure
            var complexNode = new JsonObject
            {
                ["simpleValue"] = JsonValue.Create(123),
                ["arrayWithValues"] = new JsonArray
                {
                    JsonValue.Create("first"),
                    JsonValue.Create(456),
                    JsonValue.Create(false)
                },
                ["nestedObject"] = new JsonObject
                {
                    ["deepValue"] = JsonValue.Create(3.14159),
                    ["deepArray"] = new JsonArray { JsonValue.Create(true), null }
                }
            };

            // Process through grain
            var result = await grain.ProcessJsonNode(complexNode);

            // Verify structure is preserved
            Assert.NotNull(result);
            Assert.Equal(123, result["simpleValue"]!.GetValue<int>());

            var array = result["arrayWithValues"] as JsonArray;
            Assert.NotNull(array);
            Assert.Equal("first", array![0]!.GetValue<string>());
            Assert.Equal(456, array[1]!.GetValue<int>());
            Assert.False(array[2]!.GetValue<bool>());

            var nested = result["nestedObject"] as JsonObject;
            Assert.NotNull(nested);
            Assert.Equal(3.14159, nested!["deepValue"]!.GetValue<double>(), 5);
        }

        /// <summary>
        /// Tests that JsonNode instances can be converted to strings within grain methods.
        /// Verifies that the grain receives valid JsonNode instances that can be manipulated.
        /// </summary>
        [Fact]
        public async Task GrainCanConvertJsonNodeToString()
        {
            var grain = _fixture.GrainFactory.GetGrain<IJsonNodeTestGrain>(4);

            // Test with JsonValue
            JsonNode valueNode = JsonValue.Create(42);
            var valueString = await grain.GetJsonString(valueNode);
            Assert.Equal("42", valueString);

            // Test with JsonObject
            JsonNode objectNode = new JsonObject { ["test"] = "value" };
            var objectString = await grain.GetJsonString(objectNode);
            Assert.Contains("\"test\":\"value\"", objectString);

            // Test with JsonArray
            JsonNode arrayNode = new JsonArray { 1, 2, 3 };
            var arrayString = await grain.GetJsonString(arrayNode);
            Assert.Equal("[1,2,3]", arrayString);
        }

        /// <summary>
        /// Tests that grain methods correctly handle null JsonNode parameters.
        /// Ensures that null values are properly serialized and don't cause exceptions.
        /// </summary>
        [Fact]
        public async Task GrainHandlesNullJsonNode()
        {
            var grain = _fixture.GrainFactory.GetGrain<IJsonNodeTestGrain>(5);

            JsonNode nullNode = null;
            var result = await grain.ProcessJsonNode(nullNode);
            Assert.Null(result);

            var stringResult = await grain.GetJsonString(nullNode);
            Assert.Equal("null", stringResult);
        }

        /// <summary>
        /// Tests thread safety and correctness with concurrent grain calls using JsonNode parameters.
        /// Verifies that multiple simultaneous grain calls with different JsonNode values don't interfere with each other.
        /// </summary>
        [Fact]
        public async Task MultipleConcurrentGrainCallsWithJsonNodes()
        {
            // Test concurrent calls to ensure thread safety
            var tasks = new Task[10];

            for (int i = 0; i < 10; i++)
            {
                var grainId = i + 100;
                var value = i * 10;
                tasks[i] = Task.Run(async () =>
                {
                    var grain = _fixture.GrainFactory.GetGrain<IJsonNodeTestGrain>(grainId);
                    JsonNode node = JsonValue.Create(value);
                    var result = await grain.ProcessJsonNode(node);
                    Assert.Equal(value, result.GetValue<int>());
                });
            }

            await Task.WhenAll(tasks);
        }
    }
}
