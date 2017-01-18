using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using System;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Summary description for JsonGrainTests
    /// </summary>
    public class JsonGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        public JsonGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("JSON"), TestCategory("GetGrain")]
        public async Task JSON_GetGrain()
        {
            int id = random.Next();
            var grain = this.GrainFactory.GetGrain<IJsonEchoGrain>(id);
            await grain.Ping();
        }

        [Fact, TestCategory("BVT"), TestCategory("JSON"), TestCategory("Echo")]
        public async Task JSON_EchoJson()
        {
            int id = random.Next();
            var grain = this.GrainFactory.GetGrain<IJsonEchoGrain>(id);

            // Compare to: SerializationTests_JObject_Example1
            const string json = 
            @"{
                CPU: 'Intel',
                Drives: [
                    'DVD read/writer',
                    '500 gigabyte hard drive'
                ]
            }";

            JObject input = JObject.Parse(json);
            JObject output = await grain.EchoJson(input);

            Assert.Equal(input.ToString(), output.ToString());
        }

        [Serializer(typeof(JObject))]
        public class JObjectSerializationExample1
        {
            [CopierMethod]
            public static object DeepCopier(object original, ICopyContext context)
            {
                // I assume JObject is immutable, so no need to deep copy.
                // Alternatively, can copy via JObject.ToString and JObject.Parse().
                return original;
            }

            [SerializerMethod]
            public static void Serializer(object untypedInput, ISerializationContext context, Type expected)
            {
                var input = (JObject)untypedInput;
                string str = input.ToString();
                SerializationManager.Serialize(str, context.StreamWriter);
            }

            [DeserializerMethod]
            public static object Deserializer(Type expected, IDeserializationContext context)
            {
                var str = (string)SerializationManager.Deserialize(typeof(string), context.StreamReader);
                return JObject.Parse(str);
            }
        }
    }
}
