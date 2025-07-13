#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests
{
    /// <summary>
    /// Tests for Orleans' JSON serialization support using System.Text.Json.
    ///
    /// Orleans provides integration with System.Text.Json for scenarios where:
    /// - JSON format is required for interoperability
    /// - Human-readable serialization is needed for debugging
    /// - Integration with external systems that expect JSON
    ///
    /// The JSON codec in Orleans:
    /// - Can be selectively applied to specific types using predicates
    /// - Supports polymorphic serialization through Orleans' type system
    /// - Provides both serialization and deep copy functionality
    /// - Can be combined with Orleans' native serialization for optimal performance
    /// </summary>
    [Trait("Category", "BVT")]
    public class JsonCodecTests : FieldCodecTester<MyJsonClass?, IFieldCodec<MyJsonClass?>>
    {
        public JsonCodecTests(ITestOutputHelper output) : base(output)
        {
        }

        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override MyJsonClass? CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello", Id = new(Guid.NewGuid()) };

        protected override MyJsonClass?[] TestValues => new MyJsonClass?[]
        {
            null,
            new MyJsonClass() { Id = new(Guid.NewGuid()) },
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20), Id = new(Guid.NewGuid()) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 6_000), Id = new(Guid.NewGuid()) },
        };

        /// <summary>
        /// Tests that custom types can be deep copied through the JSON codec.
        /// Verifies Orleans' deep copy functionality with JSON serialization.
        /// </summary>
        [Fact]
        public void JsonSerializerDeepCopy()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi", Id = new(Guid.NewGuid()) };

            // Test typed copier
            var typedCopier = ServiceProvider.GetRequiredService<DeepCopier<MyJsonClass>>();
            var typedResult = typedCopier.Copy(original);
            Assert.Equal(original.IntProperty, typedResult.IntProperty);
            Assert.Equal(original.SubTypeProperty, typedResult.SubTypeProperty);

            // Test untyped copier
            var untypedCopier = ServiceProvider.GetRequiredService<DeepCopier>();
            var untypedResult = (MyJsonClass)untypedCopier.Copy((object)original);
            Assert.Equal(original.IntProperty, untypedResult.IntProperty);
            Assert.Equal(original.SubTypeProperty, untypedResult.SubTypeProperty);
        }

        /// <summary>
        /// Tests round-trip serialization through Orleans' codec and serializer systems.
        /// Verifies both typed and untyped serialization paths work correctly.
        /// </summary>
        [Fact]
        public void JsonSerializerRoundTrip()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi", Id = new(Guid.NewGuid()) };

            // Test through codec
            var codecResult = RoundTripThroughCodec(original);
            Assert.Equal(original.IntProperty, codecResult.IntProperty);
            Assert.Equal(original.SubTypeProperty, codecResult.SubTypeProperty);

            // Test through untyped serializer
            var untypedResult = RoundTripThroughUntypedSerializer(original, out _);
            var result = Assert.IsType<MyJsonClass>(untypedResult);
            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        /// <summary>
        /// Verifies JsonValue can be serialized when stored as JsonNode.
        /// </summary>
        [Fact]
        public void CanSerializeJsonValue_AsJsonNode()
        {
            // Test untyped serialization (the core issue scenario)
            var jsonValueInt = JsonValue.Create(1);
            var jsonValueString = JsonValue.Create("hello");
            var jsonValueBool = JsonValue.Create(true);

            var deserializedInt = RoundTripThroughUntypedSerializer(jsonValueInt, out _);
            Assert.Equal(JsonSerializer.Serialize(jsonValueInt), JsonSerializer.Serialize(deserializedInt));

            var deserializedString = RoundTripThroughUntypedSerializer(jsonValueString, out _);
            Assert.Equal(JsonSerializer.Serialize(jsonValueString), JsonSerializer.Serialize(deserializedString));

            var deserializedBool = RoundTripThroughUntypedSerializer(jsonValueBool, out _);
            Assert.Equal(JsonSerializer.Serialize(jsonValueBool), JsonSerializer.Serialize(deserializedBool));

            // Test typed serialization (for completeness)
            var serializer = ServiceProvider.GetRequiredService<Serializer>();
            JsonNode jsonNode = JsonValue.Create(1);

            var serialized = serializer.SerializeToArray(jsonNode);
            var deserialized = serializer.Deserialize<JsonNode>(serialized);
            Assert.Equal(1, deserialized.GetValue<int>());
        }

        /// <summary>
        /// Tests serialization of all JsonNode subtypes through Orleans.
        /// Verifies polymorphic serialization works correctly.
        /// </summary>
        [Fact]
        public void CanSerializeAllJsonNodeTypes()
        {
            var serializer = ServiceProvider.GetRequiredService<Serializer>();

            // Test JsonValue
            JsonNode valueNode = JsonValue.Create(42);
            var valueDeserialized = serializer.Deserialize<JsonNode>(serializer.SerializeToArray(valueNode));
            Assert.Equal(42, valueDeserialized.GetValue<int>());

            // Test JsonObject
            JsonNode objectNode = new JsonObject { ["key"] = "value", ["number"] = 123 };
            var objectDeserialized = serializer.Deserialize<JsonNode>(serializer.SerializeToArray(objectNode));
            Assert.IsType<JsonObject>(objectDeserialized);
            Assert.Equal("value", objectDeserialized["key"]!.GetValue<string>());

            // Test JsonArray
            JsonNode arrayNode = new JsonArray { 1, 2, 3 };
            var arrayDeserialized = serializer.Deserialize<JsonNode>(serializer.SerializeToArray(arrayNode));
            Assert.IsType<JsonArray>(arrayDeserialized);
            Assert.Equal(3, ((JsonArray)arrayDeserialized).Count);

            // Test complex nested structure
            var complexNode = new JsonObject
            {
                ["array"] = new JsonArray { 1, 2, 3 },
                ["object"] = new JsonObject { ["nested"] = true },
                ["value"] = JsonValue.Create("test"),
                ["null"] = null
            };

            var complexDeserialized = serializer.Deserialize<JsonNode>(serializer.SerializeToArray(complexNode));
            Assert.NotNull(complexDeserialized);
            Assert.IsType<JsonArray>(complexDeserialized["array"]);
            Assert.IsType<JsonObject>(complexDeserialized["object"]);
            Assert.IsAssignableFrom<JsonValue>(complexDeserialized["value"]);
            Assert.Null(complexDeserialized["null"]);
        }

        /// <summary>
        /// Tests Orleans serialization of JsonDocument and JsonElement types.
        /// Verifies these types work correctly with Orleans' serialization system.
        /// </summary>
        [Fact]
        public void CanSerializeJsonDocumentAndElement()
        {
            var serializer = ServiceProvider.GetRequiredService<Serializer>();

            var jsonString = @"{
                ""data"": [1, 2, 3],
                ""metadata"": {
                    ""version"": 1,
                    ""active"": true
                }
            }";

            using var doc = JsonDocument.Parse(jsonString);

            // Test JsonElement serialization
            var element = doc.RootElement;
            var serializedElement = serializer.SerializeToArray(element);
            var deserializedElement = serializer.Deserialize<JsonElement>(serializedElement);

            Assert.Equal(JsonValueKind.Object, deserializedElement.ValueKind);
            Assert.Equal(3, deserializedElement.GetProperty("data").GetArrayLength());
            Assert.True(deserializedElement.GetProperty("metadata").GetProperty("active").GetBoolean());

            // Test JsonDocument serialization
            var serializedDoc = serializer.SerializeToArray(doc);
            var deserializedDoc = serializer.Deserialize<JsonDocument>(serializedDoc);

            Assert.NotNull(deserializedDoc);
            Assert.Equal(doc.RootElement.GetProperty("data").GetArrayLength(),
                        deserializedDoc.RootElement.GetProperty("data").GetArrayLength());

            deserializedDoc.Dispose();
        }
    }

    /// <summary>
    /// Tests for the JsonCodec deep copy functionality.
    /// Focuses on Orleans' deep copy integration with JSON types.
    /// </summary>
    [Trait("Category", "BVT")]
    public class JsonCodecCopierTests(ITestOutputHelper output) : CopierTester<MyJsonClass?, IDeepCopier<MyJsonClass?>>(output)
    {
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override IDeepCopier<MyJsonClass?> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyJsonClass?>();

        protected override MyJsonClass? CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello", Id = new(Guid.NewGuid()) };

        protected override MyJsonClass?[] TestValues => new MyJsonClass?[]
        {
            null,
            new MyJsonClass() { Id = new(Guid.NewGuid()) },
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20), Id = new(Guid.NewGuid()) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 6_000), Id = new(Guid.NewGuid()) },
        };

        /// <summary>
        /// Tests deep copying of native JSON types through Orleans.
        /// Verifies the copier works with JsonNode subtypes.
        /// </summary>
        [Fact]
        public void CanCopyNativeJsonTypes()
        {
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();

            var jsonValue = JsonValue.Create(42);
            var copiedValue = copier.Copy(jsonValue);
            Assert.Equal(JsonSerializer.Serialize(jsonValue), JsonSerializer.Serialize(copiedValue));

            // Test JsonArray copying
            JsonArray jsonArray = new JsonArray([JsonValue.Create(true), JsonValue.Create(42), JsonValue.Create("hello")]);
            var copiedArray = copier.Copy(jsonArray);
            Assert.Equal(JsonSerializer.Serialize(jsonArray), JsonSerializer.Serialize(copiedArray));

            // Test JsonObject copying
            JsonObject? jsonObject = JsonSerializer.Deserialize<JsonObject>("{\"foo\": \"bar\"}");
            var copiedObject = copier.Copy(jsonObject);
            Assert.Equal(JsonSerializer.Serialize(jsonObject), JsonSerializer.Serialize(copiedObject));
        }

        /// <summary>
        /// Tests deep copying of JsonDocument and JsonElement.
        /// Verifies copies are independent from originals.
        /// </summary>
        [Fact]
        public void CanCopyJsonDocumentAndElement()
        {
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();

            var jsonString = "{\"name\":\"test\",\"value\":42}";
            using var doc = JsonDocument.Parse(jsonString);

            // Test JsonElement copy
            var element = doc.RootElement;
            var copiedElement = copier.Copy(element);
            Assert.Equal(element.GetRawText(), copiedElement.GetRawText());

            // Test JsonDocument copy
            var copiedDocument = copier.Copy(doc);
            Assert.NotSame(doc, copiedDocument);
            Assert.Equal(doc.RootElement.GetRawText(), copiedDocument!.RootElement.GetRawText());

            copiedDocument!.Dispose();
        }
    }
}
