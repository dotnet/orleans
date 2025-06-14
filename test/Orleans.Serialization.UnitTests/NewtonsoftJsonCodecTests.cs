using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests
{
    /// <summary>
    /// Tests for Orleans' Newtonsoft.Json serialization support.
    /// 
    /// Orleans provides integration with Newtonsoft.Json (Json.NET) for scenarios requiring:
    /// - Compatibility with existing Newtonsoft.Json configurations
    /// - Advanced JSON features not available in System.Text.Json
    /// - Integration with systems already using Newtonsoft.Json
    /// 
    /// The Newtonsoft.Json codec in Orleans:
    /// - Supports all Json.NET features including custom converters
    /// - Can be selectively applied using type predicates
    /// - Provides both serialization and deep copy functionality
    /// - Handles Json.NET specific types like JObject, JArray, etc.
    /// 
    /// This integration is particularly useful when migrating existing systems
    /// to Orleans that already have complex Newtonsoft.Json configurations.
    /// </summary>
    [Trait("Category", "BVT")]
    public class NewtonsoftJsonCodecTests : FieldCodecTester<MyNewtonsoftJsonClass, IFieldCodec<MyNewtonsoftJsonClass>>
    {
        public NewtonsoftJsonCodecTests(ITestOutputHelper output) : base(output)
        {
        }

        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddNewtonsoftJsonSerializer(isSupported: type => type.GetCustomAttribute<MyNewtonsoftJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override MyNewtonsoftJsonClass CreateValue() => new MyNewtonsoftJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override int[] MaxSegmentSizes => new[] { 840 };

        protected override MyNewtonsoftJsonClass[] TestValues => new MyNewtonsoftJsonClass[]
        {
            null,
            new MyNewtonsoftJsonClass(),
            new MyNewtonsoftJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyNewtonsoftJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 4097) },
        };

        [Fact]
        public void NewtonsoftJsonDeepCopyTyped()
        {
            var original = new MyNewtonsoftJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var copier = ServiceProvider.GetRequiredService<DeepCopier<MyNewtonsoftJsonClass>>();
            var result = copier.Copy(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void NewtonsoftJsonDeepCopyUntyped()
        {
            var original = new MyNewtonsoftJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();
            var result = (MyNewtonsoftJsonClass)copier.Copy((object)original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void NewtonsoftJsonRoundTripThroughCodec()
        {
            var original = new MyNewtonsoftJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var result = RoundTripThroughCodec(original);

            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void NewtonsoftJsonRoundTripThroughUntypedSerializer()
        {
            var original = new MyNewtonsoftJsonClass { IntProperty = 30, SubTypeProperty = "hi" };
            var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

            var result = Assert.IsType<MyNewtonsoftJsonClass>(untypedResult);
            Assert.Equal(original.IntProperty, result.IntProperty);
            Assert.Equal(original.SubTypeProperty, result.SubTypeProperty);
        }

        [Fact]
        public void CanSerializeNativeJsonTypes()
        {
            var jsonArray = Newtonsoft.Json.JsonConvert.DeserializeObject<JArray>("[1, true, \"three\", {\"foo\": \"bar\"}]");
            var jsonObject = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>("{\"foo\": \"bar\"}");
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();

            var deserializedArray = RoundTripThroughUntypedSerializer(jsonArray, out _);
            Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(jsonArray), Newtonsoft.Json.JsonConvert.SerializeObject(deserializedArray));

            var deserializedObject = RoundTripThroughUntypedSerializer(jsonObject, out _);
            Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(jsonObject), Newtonsoft.Json.JsonConvert.SerializeObject(deserializedObject));
        }
    }

    [Trait("Category", "BVT")]
    public class NewtonsoftJsonCodecCopierTests : CopierTester<MyNewtonsoftJsonClass, IDeepCopier<MyNewtonsoftJsonClass>>
    {
        public NewtonsoftJsonCodecCopierTests(ITestOutputHelper output) : base(output)
        {
        }

        protected override void Configure(ISerializerBuilder builder)
        {
            builder.AddNewtonsoftJsonSerializer(isSupported: type => type.GetCustomAttribute<MyNewtonsoftJsonSerializableAttribute>(inherit: false) is not null);
        }

        protected override IDeepCopier<MyNewtonsoftJsonClass> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyNewtonsoftJsonClass>();

        protected override MyNewtonsoftJsonClass CreateValue() => new MyNewtonsoftJsonClass { IntProperty = 30, SubTypeProperty = "hello" };

        protected override MyNewtonsoftJsonClass[] TestValues => new MyNewtonsoftJsonClass[]
        {
            null,
            new MyNewtonsoftJsonClass(),
            new MyNewtonsoftJsonClass() { IntProperty = 150, SubTypeProperty = new string('c', 20) },
            new MyNewtonsoftJsonClass() { IntProperty = -150_000, SubTypeProperty = new string('c', 4097) },
        };

        [Fact]
        public void CanCopyNativeJsonTypes()
        {
            var jsonArray = Newtonsoft.Json.JsonConvert.DeserializeObject<JArray>("[1, true, \"three\", {\"foo\": \"bar\"}]");
            var jsonObject = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>("{\"foo\": \"bar\"}");
            var copier = ServiceProvider.GetRequiredService<DeepCopier>();

            var deserializedArray = copier.Copy(jsonArray);
            Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(jsonArray), Newtonsoft.Json.JsonConvert.SerializeObject(deserializedArray));

            var deserializedObject = copier.Copy(jsonObject);
            Assert.Equal(Newtonsoft.Json.JsonConvert.SerializeObject(jsonObject), Newtonsoft.Json.JsonConvert.SerializeObject(deserializedObject));
        }
    }
}