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
    public class NewtonsoftJsonCodecTests : FieldCodecTester<MyJsonClass, IFieldCodec<MyJsonClass>>
    {
        public NewtonsoftJsonCodecTests(ITestOutputHelper output) : base(output)
        {
        }

        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddNewtonsoftJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override MyJsonClass CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override int[] MaxSegmentSizes => new[] { 840 };

        protected override MyJsonClass[] TestValues => new MyJsonClass[]
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
    public class NewtonsoftJsonCodecCopierTests : CopierTester<MyJsonClass, IDeepCopier<MyJsonClass>>
    {
        public NewtonsoftJsonCodecCopierTests(ITestOutputHelper output) : base(output)
        {
        }

        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddNewtonsoftJsonSerializer(isSupported: type => type.GetCustomAttribute<MyJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override IDeepCopier<MyJsonClass> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyJsonClass>();

        protected override MyJsonClass CreateValue() => new MyJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override MyJsonClass[] TestValues => new MyJsonClass[]
        {
            null,
            new MyJsonClass(),
            new MyJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 4097) },
        };
    }
}