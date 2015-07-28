using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;

namespace Tester
{
    [TestClass]
    public class SerializationTests_InnerTypes
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void InnerTypeSerializationTests_DictionaryTest_TypeNameHandling()
        {
            var original = new RootType();
            var str = JsonConvert.SerializeObject(original, JsonSerialization_Example2.Settings);
            var jsonDeser = JsonConvert.DeserializeObject<RootType>(str, JsonSerialization_Example2.Settings);
            // JsonConvert fully deserializes the object back into RootType and InnerType since we are using TypeNameHandling.All setting.
            Assert.AreEqual(typeof(InnerType), original.MyDictionary["obj1"].GetType());
            Assert.AreEqual(typeof(InnerType), jsonDeser.MyDictionary["obj1"].GetType());
            Assert.AreEqual(original, jsonDeser);

            // Orleans's SerializationManager also deserializes everything correctly, but it serializes it into its own binary format
            var orleansDeser = SerializationManager.RoundTripSerializationForTesting(original);
            Assert.AreEqual(typeof(InnerType), jsonDeser.MyDictionary["obj1"].GetType());
            Assert.AreEqual(original, orleansDeser);
    
            Console.WriteLine("Done OK.");
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void InnerTypeSerializationTests_DictionaryTest_NoTypeNameHandling()
        {
            var original = new RootType();
            var str = JsonConvert.SerializeObject(original);
            var jsonDeser = JsonConvert.DeserializeObject<RootType>(str);
            // Here we don't use TypeNameHandling.All setting, therefore the full type information is not preserved.
            // As a result JsonConvert leaves the inner types as JObjects after Deserialization.
            Assert.AreEqual(typeof(InnerType), original.MyDictionary["obj1"].GetType());
            Assert.AreEqual(typeof(JObject), jsonDeser.MyDictionary["obj1"].GetType());
            // The below Assert actualy fails since jsonDeser has JObjects instead of InnerTypes!
            // Assert.AreEqual(original, jsonDeser);
    
            // If we now take this half baked object: RootType at the root and JObject in the leaves
            // and pass it through .NET binary serializer it would fail on this object since JObject is not marked as [Serializable]
            // and therefore .NET binary serializer cannot serialize it.

            // If we now take this half baked object: RootType at the root and JObject in the leaves
            // and pass it through Orleans serializer it would work:
            // RootType will be serialized with Orleans custom serializer that will be generated since RootType is defined
            // in GrainInterfaces assembly and markled as [Serializable].
            // JObject that is referenced from RootType will be serialized with JsonSerialization_Example2 below.

            var orleansJsonDeser = SerializationManager.RoundTripSerializationForTesting(jsonDeser);
            Assert.AreEqual(typeof(JObject), orleansJsonDeser.MyDictionary["obj1"].GetType());
            // The below assert fails, but only since JObject does not correctly implement Equals.
            //Assert.AreEqual(jsonDeser, orleansJsonDeser);

            Console.WriteLine("Done OK.");
        }
    }

    /// <summary>
    /// Provides support for serializing JSON values.
    /// </summary>
    [RegisterSerializer]
    public class JsonSerialization_Example2
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            TypeNameHandling = TypeNameHandling.All
        };

        static JsonSerialization_Example2()
        {
            Register();
        }

        public static object DeepCopier(object original)
        {
            return original;
        }

        public static void Serialize(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var str = JsonConvert.SerializeObject(obj, Settings);
            SerializationManager.SerializeInner(str, stream, typeof(string));
        }

        public static object Deserialize(Type expected, BinaryTokenStreamReader stream)
        {
            var str = (string)SerializationManager.DeserializeInner(typeof(string), stream);
            return JsonConvert.DeserializeObject(str, expected);
        }

        public static void Register()
        {
            foreach (var type in
                    new[]
                    {
                        typeof(JObject), typeof(JArray), typeof(JToken), typeof(JValue), typeof(JProperty), typeof(JConstructor), 
                    })
            {
                SerializationManager.Register(type, DeepCopier, Serialize, Deserialize);
            }
        }
    }
}
