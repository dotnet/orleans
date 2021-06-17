using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    using System;
    using System.Text;
    using FluentAssertions.Common;
    using Microsoft.Extensions.Hosting;
    using Microsoft.OData.Json;
    using Newtonsoft.Json;

    using Orleans.Serialization;
    using Orleans.Serialization.Serializers;

    [TestCategory("Serialization"), TestCategory("BVT")]
    public class OrleansJsonSerializerTests
    {
        private readonly SerializationTestEnvironment environment;

        public OrleansJsonSerializerTests()
        {
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(
                builder => builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IGeneralizedCodec>(new NewtonsoftJsonCodec(isSupportedFunc: type => type.HasAttribute<JsonTypeAttribute>()));
                }));
        }

        [Fact]
        public void OrleansJsonSerializer_ExternalSerializer_Client()
        {
            TestSerializationRoundTrip(this.environment.Serializer);
        }

        [Fact]
        public void OrleansJsonSerializer_ExternalSerializer_Silo()
        {
            var silo = new HostBuilder()
                .UseOrleans(siloBuilder =>
                {
                    siloBuilder
                    .Configure<ClusterOptions>(o => o.ClusterId = o.ServiceId = "s")
                    .UseLocalhostClustering()
                    .ConfigureServices(
                    services =>
                    {
                        services.AddSingleton<IGeneralizedCodec>(new NewtonsoftJsonCodec(isSupportedFunc: type => type.HasAttribute<JsonTypeAttribute>()));
                    });
                })
                .Build();
            var serializer = silo.Services.GetRequiredService<Serializer>();
            TestSerializationRoundTrip(serializer);
        }

        private static void TestSerializationRoundTrip(Serializer serializer)
        {
            var data = new JsonPoco {Prop = "some data"};
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

            return result.ToString();
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
        }
    }
}