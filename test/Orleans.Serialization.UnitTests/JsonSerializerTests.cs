#nullable enable
using System;
using System.Reflection;
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

        [Fact]
        public void JsonSerializerDeepCopyTyped()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi", Id = new(Guid.NewGuid()) };
            var copier = ServiceProvider.GetRequiredService<DeepCopier<MyJsonClass>>();
            var result = copier.Copy(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void JsonSerializerDeepCopyUntyped()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi", Id = new(Guid.NewGuid()) };
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();
            var result = (MyJsonClass)copier.Copy((object)original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void JsonSerializerRoundTripThroughCodec()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi", Id = new(Guid.NewGuid()) };
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void JsonSerializerRoundTripThroughUntypedSerializer()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi", Id = new(Guid.NewGuid()) };
            var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

            var result = Assert.IsType<MyJsonClass>(untypedResult);
            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void CanSerializeNativeJsonTypes()
        {
            JsonArray jsonArray = new JsonArray([JsonValue.Create(true), JsonValue.Create(42), JsonValue.Create("hello")]);
            JsonObject? jsonObject = System.Text.Json.JsonSerializer.Deserialize<JsonObject>("{\"foo\": \"bar\"}");

            var deserializedArray = RoundTripThroughUntypedSerializer(jsonArray, out _);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonArray), System.Text.Json.JsonSerializer.Serialize(deserializedArray));

            var deserializedObject = RoundTripThroughUntypedSerializer(jsonObject, out _);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonObject), System.Text.Json.JsonSerializer.Serialize(deserializedObject));
        }

        [Fact]
        public void CanSerializeJsonValue_CreatedWithPrimitives()
        {
            // This reproduces the issue described in GitHub issue #9568
            var jsonValueInt = JsonValue.Create(1);
            var jsonValueString = JsonValue.Create("hello");
            var jsonValueBool = JsonValue.Create(true);

            // These should not throw InvalidCastException
            var deserializedInt = RoundTripThroughUntypedSerializer(jsonValueInt, out _);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonValueInt), System.Text.Json.JsonSerializer.Serialize(deserializedInt));

            var deserializedString = RoundTripThroughUntypedSerializer(jsonValueString, out _);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonValueString), System.Text.Json.JsonSerializer.Serialize(deserializedString));

            var deserializedBool = RoundTripThroughUntypedSerializer(jsonValueBool, out _);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonValueBool), System.Text.Json.JsonSerializer.Serialize(deserializedBool));
        }

        [Fact]
        public void CanSerializeJsonNode_AsJsonValue()
        {
            // This reproduces the grain scenario described in GitHub issue #9568
            JsonNode jsonNode = JsonValue.Create(1);

            // This should not throw InvalidCastException when the parameter is JsonNode but the value is JsonValue
            var deserialized = RoundTripThroughUntypedSerializer(jsonNode, out _);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonNode), System.Text.Json.JsonSerializer.Serialize(deserialized));
        }
    }

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

        [Fact]
        public void CanCopyNativeJsonTypes()
        {
            JsonArray jsonArray = new JsonArray([JsonValue.Create(true), JsonValue.Create(42), JsonValue.Create("hello")]);
            JsonObject? jsonObject = System.Text.Json.JsonSerializer.Deserialize<JsonObject>("{\"foo\": \"bar\"}");
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();

            var deserializedArray = copier.Copy(jsonArray);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonArray), System.Text.Json.JsonSerializer.Serialize(deserializedArray));

            var deserializedObject = copier.Copy(jsonObject);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonObject), System.Text.Json.JsonSerializer.Serialize(deserializedObject));
        }

        [Fact]
        public void CanCopyJsonValue_CreatedWithPrimitives()
        {
            // This reproduces the issue described in GitHub issue #9568 for copying
            var jsonValueInt = JsonValue.Create(1);
            var jsonValueString = JsonValue.Create("hello");
            var jsonValueBool = JsonValue.Create(true);
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();

            // These should not throw InvalidCastException
            var copiedInt = copier.Copy(jsonValueInt);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonValueInt), System.Text.Json.JsonSerializer.Serialize(copiedInt));

            var copiedString = copier.Copy(jsonValueString);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonValueString), System.Text.Json.JsonSerializer.Serialize(copiedString));

            var copiedBool = copier.Copy(jsonValueBool);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(jsonValueBool), System.Text.Json.JsonSerializer.Serialize(copiedBool));
        }
    }
}