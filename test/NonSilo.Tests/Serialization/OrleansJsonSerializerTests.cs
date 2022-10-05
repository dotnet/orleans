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
    using Newtonsoft.Json;
    using Orleans.Runtime;
    using Orleans.Serialization;

    [TestCategory("Serialization"), TestCategory("BVT")]
    public class OrleansJsonSerializerTests
    {
        private readonly SerializationTestEnvironment environment;

        public OrleansJsonSerializerTests()
        {
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(
                builder => builder.Configure<SerializationProviderOptions>(
                    options => options.SerializationProviders.Add(typeof(OrleansJsonSerializer))));
        }

        [Fact]
        public void OrleansJsonSerializer_ExternalSerializer_Client()
        {
            TestSerializationRoundTrip1(this.environment.SerializationManager);
        }

        [Fact]
        public void OrleansJsonSerializer_ExternalSerializer_Silo()
        {
            var silo = this.BuildTestSilo();
            var serializationManager = silo.Services.GetRequiredService<SerializationManager>();
            TestSerializationRoundTrip1(serializationManager);
        }

        [Fact]
        public void OrleansJsonSerializer_CanModifySerializerSettings()
        {
            var silo = this.BuildTestSilo((siloHostBuilder) =>
            {
                return siloHostBuilder.Configure<OrleansJsonSerializerOptions>(o =>
                    o.ConfigureSerializerSettings(this.ConfigureSerializerSettings));
            });

            var serializationManager = silo.Services.GetRequiredService<SerializationManager>();
            TestSerializationRoundTrip2(serializationManager);
        }


        private static void TestSerializationRoundTrip1(SerializationManager serializationManager)
        {
            var data = new JsonPoco {Prop = "some data"};
            var serialized = serializationManager.SerializeToByteArray(data);
            var subSequence = Encoding.UTF8.GetBytes("crazy_name");

            // The serialized data should have our custom [JsonProperty] name, 'crazy_name', in it.
            Assert.Contains(ToString(subSequence), ToString(serialized));

            var deserialized = serializationManager.DeserializeFromByteArray<JsonPoco>(serialized);

            Assert.Equal(data.Prop, deserialized.Prop);
        }

        private static void TestSerializationRoundTrip2(SerializationManager serializationManager)
        {
            var data = new JsonPocoWithDefaults { Prop = false };
            var serialized = serializationManager.SerializeToByteArray(data);

            var deserialized = serializationManager.DeserializeFromByteArray<JsonPocoWithDefaults>(serialized);

            // With default values included, we expect this type will round-trip correctly.
            // The value of "Prop" must be included in the JSON even though it matches the default for the type.
            Assert.Equal(data.Prop, deserialized.Prop);
        }

        private ISiloHost BuildTestSilo(Func<ISiloHostBuilder, ISiloHostBuilder> configureSiloHostBuilder = null)
        {
            var siloHostBuilder = new SiloHostBuilder()
                .Configure<ClusterOptions>(o => o.ClusterId = o.ServiceId = "s")
                .Configure<SerializationProviderOptions>(o =>
                    o.SerializationProviders.Add(typeof(OrleansJsonSerializer)))
                .UseLocalhostClustering();

            if (configureSiloHostBuilder != null)
            {
                siloHostBuilder = configureSiloHostBuilder(siloHostBuilder);
            }

            return siloHostBuilder.Build();
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

        private void ConfigureSerializerSettings(JsonSerializerSettings jsonSerializerSettings)
        {
            var serviceProvider = this.environment.Services;

            var typeResolver = serviceProvider.GetService<ITypeResolver>();
            var grainFactory = serviceProvider.GetService<GrainFactory>();

            JsonSerializerSettings defaultSerializerSettings = OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory);

            // Validate that we received the pre-configured default settings:
            Assert.Equal(defaultSerializerSettings.TypeNameHandling, jsonSerializerSettings.TypeNameHandling);
            Assert.Equal(defaultSerializerSettings.PreserveReferencesHandling, jsonSerializerSettings.PreserveReferencesHandling);
            Assert.Equal(defaultSerializerSettings.DefaultValueHandling, jsonSerializerSettings.DefaultValueHandling);
            Assert.Equal(defaultSerializerSettings.MissingMemberHandling, jsonSerializerSettings.MissingMemberHandling);
            Assert.Equal(defaultSerializerSettings.NullValueHandling, jsonSerializerSettings.NullValueHandling);
            Assert.Equal(defaultSerializerSettings.ConstructorHandling, jsonSerializerSettings.ConstructorHandling);
            Assert.Equal(defaultSerializerSettings.TypeNameAssemblyFormatHandling, jsonSerializerSettings.TypeNameAssemblyFormatHandling);
            Assert.Equal(defaultSerializerSettings.Formatting, jsonSerializerSettings.Formatting);
            Assert.Equal(defaultSerializerSettings.SerializationBinder.GetType(), jsonSerializerSettings.SerializationBinder.GetType());
            Assert.NotEmpty(jsonSerializerSettings.Converters);

            // Change the settings:
            jsonSerializerSettings.DefaultValueHandling = DefaultValueHandling.Include;
        }

        public class JsonPoco
        {
            [JsonProperty("crazy_name")]
            public string Prop { get; set; }
        }

        public class JsonPocoWithDefaults
        {
            public bool Prop { get; set; } = true;
        }
    }
}