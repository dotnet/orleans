using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO.Pipelines;
using Xunit;
using System.Reflection;
using Orleans.Serialization.TestKit;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT")]
    public class JsonCodecTests : FieldCodecTester<object, JsonCodec>
    {
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override object CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override object[] TestValues => new object[]
        {
            null,
            new MyJsonClass(),
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 6_000) },
        };

        [Fact]
        public void JsonSerializerDeepCopyTyped()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var copier = ServiceProvider.GetRequiredService<DeepCopier<MyJsonClass>>();
            var result = copier.Copy(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void JsonSerializerDeepCopyUntyped()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();
            var result = (MyJsonClass)copier.Copy((object)original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void JsonSerializerRoundTripThroughCodec()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void JsonSerializerRoundTripThroughUntypedSerializer()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

            var result = Assert.IsType<MyJsonClass>(untypedResult);
            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }
    }

    [Trait("Category", "BVT")]
    public class JsonCodecCopierTests : CopierTester<object, JsonCodec>
    {
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override object CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override object[] TestValues => new object[]
        {
            null,
            new MyJsonClass(),
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 6_000) },
        };
    }
}