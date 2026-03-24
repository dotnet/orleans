using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using System.Reflection;
using TestExtensions;
using Xunit;
using System.Text;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;

using Orleans.Serialization;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Streaming.EventHubs;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using Orleans.Metadata;

namespace UnitTests.Serialization
{
    [TestCategory("Serialization"), TestCategory("BVT")]
    public class ExternalCodecTests
    {
        private readonly SerializationTestEnvironment environment;

        public ExternalCodecTests()
        {
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(
                builder =>
                    builder.ConfigureServices(services =>
                        services.AddSerializer(serializerBuilder =>
                        {
                            serializerBuilder.AddNewtonsoftJsonSerializer(type => type.GetCustomAttribute<JsonTypeAttribute>() is not null);
                        })));
        }

        [Fact]
        public void NewtonsoftJsonCodec_ExternalSerializer_Client()
        {
            TestSerializationRoundTrip(this.environment.Serializer);
        }

        [Fact]
        public void NewtonsoftJsonCodec_ExternalSerializer_Silo()
        {
            var silo = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                    .Configure<ClusterOptions>(o => o.ClusterId = o.ServiceId = "s")
                    .UseLocalhostClustering()
                    .ConfigureServices(services =>
                        services.AddSerializer(serializerBuilder =>
                        {
                            serializerBuilder.AddNewtonsoftJsonSerializer(type => type.GetCustomAttribute<JsonTypeAttribute>() is not null);
                        }));
                })
                .Build();
            var serializer = silo.Services.GetRequiredService<Serializer>();
            TestSerializationRoundTrip(serializer);
        }

        [Fact]
        public void NewtonsoftJsonCodec_CanModifySerializerSettings()
        {
            var silo = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                    .Configure<ClusterOptions>(o => o.ClusterId = o.ServiceId = "s")
                    .Configure<OrleansJsonSerializerOptions>(options => options.JsonSerializerSettings.DefaultValueHandling = DefaultValueHandling.Include)
                    .UseLocalhostClustering();
                })
                .Build();
            var serializer = silo.Services.GetRequiredService<OrleansJsonSerializer>();
            var data = new JsonPoco();
            var serialized = serializer.Serialize(data, typeof(JsonPoco));
            Assert.Contains("some_flag", serialized);
        }

        [Fact]
        public void NewtonsoftJsonCodec_DoesNotSerializeFrameworkTypes()
        {
            var silo = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.Services.AddSerializer(serializerBuilder => serializerBuilder.AddNewtonsoftJsonSerializer(type =>
                        {
                            Assert.Fail($"Custom type filter should not be consulted for any type in this test, but it was used for type {type}.");
                            return true;
                        }));
                    siloBuilder.UseLocalhostClustering();
                })
                .Build();
            var services = silo.Services;
            var serializer = services.GetRequiredService<Serializer>();
            var generatedGrainReferenceType = services.GetRequiredService<IOptions<TypeManifestOptions>>().Value
                .InterfaceProxies.First(i => ! i.IsGenericType && i.Assembly.GetCustomAttribute<FrameworkPartAttribute>() is null);
            var codecProvider = services.GetRequiredService<CodecProvider>();
            foreach (var type in new[] { typeof(SiloAddress), typeof(GrainReference), typeof(EventHubBatchContainer), generatedGrainReferenceType })
            {
                var codec = codecProvider.GetCodec(type);
                Assert.IsNotType<NewtonsoftJsonCodec>(codec);

                var copier = codecProvider.GetDeepCopier(type);
                Assert.IsNotType<NewtonsoftJsonCodec>(copier);
            }
        }

        [Fact]
        public void SystemTextJsonCodec_DoesNotSerializeFrameworkTypes()
        {
            var silo = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.Services.AddSerializer(serializerBuilder => serializerBuilder.AddJsonSerializer(type =>
                        {
                            Assert.Fail($"Custom type filter should not be consulted for any type in this test, but it was used for type {type}.");
                            return true;
                        }));
                    siloBuilder.UseLocalhostClustering();
                })
                .Build();
            var services = silo.Services;
            var serializer = services.GetRequiredService<Serializer>();
            var generatedGrainReferenceType = services.GetRequiredService<IOptions<TypeManifestOptions>>().Value
                .InterfaceProxies.First(i => ! i.IsGenericType && i.Assembly.GetCustomAttribute<FrameworkPartAttribute>() is null);
            var codecProvider = services.GetRequiredService<CodecProvider>();
            foreach (var type in new[] { typeof(SiloAddress), typeof(GrainReference), typeof(EventHubBatchContainer), generatedGrainReferenceType })
            {
                var codec = codecProvider.GetCodec(type);
                Assert.IsNotType<NewtonsoftJsonCodec>(codec);

                var copier = codecProvider.GetDeepCopier(type);
                Assert.IsNotType<NewtonsoftJsonCodec>(copier);
            }
        }

        [Fact]
        public void ProtocolBuffersCodec_DoesNotSerializeFrameworkTypes()
        {
            var silo = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.Services.AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer(type =>
                        {
                            Assert.Fail($"Custom type filter should not be consulted for any type in this test, but it was used for type {type}.");
                            return true;
                        },
                        type =>
                        {
                            Assert.Fail($"Custom type filter should not be consulted for any type in this test, but it was used for type {type}.");
                            return true;
                        }));
                    siloBuilder.UseLocalhostClustering();
                })
                .Build();
            var services = silo.Services;
            var serializer = services.GetRequiredService<Serializer>();
            var generatedGrainReferenceType = services.GetRequiredService<IOptions<TypeManifestOptions>>().Value
                .InterfaceProxies.First(i => ! i.IsGenericType && i.Assembly.GetCustomAttribute<FrameworkPartAttribute>() is null);
            var codecProvider = services.GetRequiredService<CodecProvider>();
            foreach (var type in new[] { typeof(SiloAddress), typeof(GrainReference), typeof(EventHubBatchContainer), generatedGrainReferenceType })
            {
                var codec = codecProvider.GetCodec(type);
                Assert.IsNotType<NewtonsoftJsonCodec>(codec);

                var copier = codecProvider.GetDeepCopier(type);
                Assert.IsNotType<NewtonsoftJsonCodec>(copier);
            }
        }

        private static void TestSerializationRoundTrip(Serializer serializer)
        {
            var data = new JsonPoco { Prop = "some data" };
            var serialized = serializer.SerializeToArray(data);
            var subSequence = Encoding.UTF8.GetBytes("crazy_name");

            // The serialized data should have our custom [JsonProperty] name, 'crazy_name', in it.
            Assert.Contains(ToString(subSequence), ToString(serialized));

            var deserialized = serializer.Deserialize<JsonPoco>(serialized);

            Assert.Equal(data.Prop, deserialized.Prop);
        }

        private static string ToString(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                result.Append($"{b:x2}");
            }

            var str = result.ToString();
            return str;
        }

        /// <summary>
        /// Use a custom attribute to distinguish types which should be serialized using the JSON codec.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
        internal class JsonTypeAttribute : Attribute
        {
        }

        [JsonType]
        public class JsonPoco
        {
            [JsonProperty("crazy_name")]
            public string Prop { get; set; }

            [JsonProperty("some_flag")]
            public bool Flag { get; set; }
        }
    }
}