using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class SerializationTestsJsonTypes
    {
        private readonly TestEnvironmentFixture fixture;

        public SerializationTestsJsonTypes(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON")]
        public void SerializationTests_JObject_Example1()
        {
            const string json = @"{ 
                    CPU: 'Intel', 
                    Drives: [ 
                            'DVD read/writer', 
                            '500 gigabyte hard drive' 
                           ] 
                   }"; 
 
            JObject input = JObject.Parse(json);
            JObject output = fixture.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.Equal(input.ToString(), output.ToString());
        }
        
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON")]
        public void SerializationTests_Json_InnerTypes_TypeNameHandling()
        {
            var original = new RootType();
            var str = JsonConvert.SerializeObject(original, JsonSerializationExample2.Settings);
            var jsonDeser = JsonConvert.DeserializeObject<RootType>(str, JsonSerializationExample2.Settings);
            // JsonConvert fully deserializes the object back into RootType and InnerType since we are using TypeNameHandling.All setting.
            Assert.Equal(typeof(InnerType), original.MyDictionary["obj1"].GetType());
            Assert.Equal(typeof(InnerType), jsonDeser.MyDictionary["obj1"].GetType());
            Assert.Equal(original, jsonDeser);

            // Orleans's SerializationManager also deserializes everything correctly, but it serializes it into its own binary format
            var orleansDeser = this.fixture.SerializationManager.RoundTripSerializationForTesting(original);
            Assert.Equal(typeof(InnerType), jsonDeser.MyDictionary["obj1"].GetType());
            Assert.Equal(original, orleansDeser);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON")]
        public void SerializationTests_Json_InnerTypes_NoTypeNameHandling()
        {
            var original = new RootType();
            var str = JsonConvert.SerializeObject(original);
            var jsonDeser = JsonConvert.DeserializeObject<RootType>(str);
            // Here we don't use TypeNameHandling.All setting, therefore the full type information is not preserved.
            // As a result JsonConvert leaves the inner types as JObjects after Deserialization.
            Assert.Equal(typeof(InnerType), original.MyDictionary["obj1"].GetType());
            Assert.Equal(typeof(JObject), jsonDeser.MyDictionary["obj1"].GetType());
            // The below Assert actualy fails since jsonDeser has JObjects instead of InnerTypes!
            // Assert.Equal(original, jsonDeser);
    
            // If we now take this half baked object: RootType at the root and JObject in the leaves
            // and pass it through .NET binary serializer it would fail on this object since JObject is not marked as [Serializable]
            // and therefore .NET binary serializer cannot serialize it.

            // If we now take this half baked object: RootType at the root and JObject in the leaves
            // and pass it through Orleans serializer it would work:
            // RootType will be serialized with Orleans custom serializer that will be generated since RootType is defined
            // in GrainInterfaces assembly and markled as [Serializable].
            // JObject that is referenced from RootType will be serialized with JsonSerialization_Example2 below.

            var orleansJsonDeser = this.fixture.SerializationManager.RoundTripSerializationForTesting(jsonDeser);
            Assert.Equal(typeof(JObject), orleansJsonDeser.MyDictionary["obj1"].GetType());
            // The below assert fails, but only since JObject does not correctly implement Equals.
            //Assert.Equal(jsonDeser, orleansJsonDeser);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON")]
        public void SerializationTests_Json_POCO()
        {
            var obj = new SimplePOCO();
            var deepCopied = this.fixture.SerializationManager.RoundTripSerializationForTesting(obj);
            Assert.Equal(typeof(SimplePOCO), deepCopied.GetType());
        }

        [Serializable]
        public class SimplePOCO
        {
            public int A { get; set; }
            public int B { get; set; }
        }

        /// <summary>
        /// A different way to configure Json serializer.
        /// </summary>
        [Serializer(typeof(JObject))]
        [Serializer(typeof(JArray))]
        [Serializer(typeof(JToken))]
        [Serializer(typeof(JValue))]
        [Serializer(typeof(JProperty))]
        [Serializer(typeof(JConstructor))]
        public class JsonSerializationExample2
        {
            internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameHandling = TypeNameHandling.All
            };

            [CopierMethod]
            public static object DeepCopier(object original, ICopyContext context)
            {
                // I assume JObject is immutable, so no need to deep copy.
                // Alternatively, can copy via JObject.ToString and JObject.Parse().
                return original;
            }

            [SerializerMethod]
            public static void Serialize(object obj, ISerializationContext context, Type expected)
            {
                var str = JsonConvert.SerializeObject(obj, Settings);
                context.SerializationManager.Serialize(str, context.StreamWriter);
            }

            [DeserializerMethod]
            public static object Deserialize(Type expected, IDeserializationContext context)
            {
                var str = (string)context.SerializationManager.Deserialize(typeof(string), context.StreamReader);
                return JsonConvert.DeserializeObject(str, expected);
            }
        }
    }
}
