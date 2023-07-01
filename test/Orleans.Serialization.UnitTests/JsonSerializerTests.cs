#nullable enable
using System;
using System.Reflection;
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
    }

    [Trait("Category", "BVT")]
    public class JsonCodecCopierTests : CopierTester<MyJsonClass?, IDeepCopier<MyJsonClass?>>
    {
        public JsonCodecCopierTests(ITestOutputHelper output) : base(output)
        {
        }

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
    }
}