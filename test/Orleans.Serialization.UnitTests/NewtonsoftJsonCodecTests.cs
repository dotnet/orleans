using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Pipelines;
using Xunit;
using System.Reflection;
using Orleans.Serialization.TestKit;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT")]
    public class NewtonsoftJsonCodecTests : FieldCodecTester<object, NewtonsoftJsonCodec>
    {
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddNewtonsoftJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override object CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override int[] MaxSegmentSizes => new[] { 840 };

        protected override object[] TestValues => new object[]
        {
            null,
            new MyJsonClass(),
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 4097) },
        };

        [Fact]
        public void NewtonsoftJsonDeepCopyTyped()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var copier = ServiceProvider.GetRequiredService<DeepCopier<MyJsonClass>>();
            var result = copier.Copy(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void NewtonsoftJsonDeepCopyUntyped()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();
            var result = (MyJsonClass)copier.Copy((object)original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void NewtonsoftJsonRoundTripThroughCodec()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void NewtonsoftJsonRoundTripThroughUntypedSerializer()
        {
            var original = new MyJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

            var result = Assert.IsType<MyJsonClass>(untypedResult);
            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }
    }

    [Trait("Category", "BVT")]
    public class NewtonsoftJsonCodecCopierTests : CopierTester<object, NewtonsoftJsonCodec>
    {
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddNewtonsoftJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override object CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override object[] TestValues => new object[]
        {
            null,
            new MyJsonClass(),
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 4097) },
        };
    }
}