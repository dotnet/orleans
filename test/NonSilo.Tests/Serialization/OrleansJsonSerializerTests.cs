using System.Reflection;
using Orleans.Runtime.Configuration;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    using System.Text;

    using Newtonsoft.Json;

    using Orleans.Serialization;

    [TestCategory("Serialization"), TestCategory("BVT")]
    public class OrleansJsonSerializerTests
    {
        private readonly SerializationTestEnvironment environment;

        public OrleansJsonSerializerTests()
        {
            var config = new ClientConfiguration { SerializationProviders = { typeof(OrleansJsonSerializer).GetTypeInfo() } };
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(config);
        }

        [Fact]
        public void OrleansJsonSerializer_ExternalSerializer()
        {
            var data = new JsonPoco { Prop = "some data" };
            var serialized = this.environment.SerializationManager.SerializeToByteArray(data);
            var subSequence = Encoding.UTF8.GetBytes("crazy_name");

            // The serialized data should have our custom [JsonProperty] name, 'crazy_name', in it.
            Assert.Contains(ToString(subSequence), ToString(serialized));

            var deserialized = this.environment.SerializationManager.DeserializeFromByteArray<JsonPoco>(serialized);

            Assert.Equal(data.Prop, deserialized.Prop);

            string ToString(byte[] bytes)
            {
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    result.Append($"{b:x2}");
                }

                return result.ToString();
            }
        }
        
        public class JsonPoco
        {
            [JsonProperty("crazy_name")]
            public string Prop { get; set; }
        }
    }
}