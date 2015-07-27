using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Orleans.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;

namespace Tester
{
    [TestClass]
    public class InnerTypeSerializationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void InnerTypeSerializationTests_DictionaryTest()
        {
            //Client create, serializes and send to the frontend server.
            var payload = new RootType();
            var str = JsonConvert.SerializeObject(payload);

            //The frontend will deserialize the incoming parameter, which will turn all itens on the dictionary into a JObject
            var desPayload = JsonConvert.DeserializeObject<RootType>(str);

            //Grain is called from the frontend server
            var smReturn = SerializationManager.RoundTripSerializationForTesting(desPayload);

            Assert.AreEqual(payload, smReturn);
        }
    }

    public class RootType
    {
        public RootType()
        {
            MyDictionary = new Dictionary<string, object>();
            MyDictionary.Add("obj1", new InnerType());
            MyDictionary.Add("obj2", new InnerType());
            MyDictionary.Add("obj3", new InnerType());
            MyDictionary.Add("obj4", new InnerType());
        }
        public Dictionary<string, object> MyDictionary { get; set; }
    }

    public class InnerType
    {
        public InnerType()
        {
            Id = Guid.NewGuid();
            Something = Id.ToString();
        }
        public Guid Id { get; set; }
        public string Something { get; set; }
    }

    /// <summary>
    /// Provides support for serializing JSON values.
    /// </summary>
    [RegisterSerializer]
    public class JsonSerialization
    {
        private static JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Initializes static members of the <see cref="JsonSerialization"/> class.
        /// </summary>
        static JsonSerialization()
        {
            Register();
        }

        /// <summary>
        /// The deep copier.
        /// </summary>
        /// <param name="original">
        /// The original.
        /// </param>
        /// <returns>
        /// The copy.
        /// </returns>
        public static object DeepCopier(object original)
        {
            return original;
        }

        /// <summary>
        /// Serializes an object to a stream.
        /// </summary>
        /// <param name="obj">
        /// The object being serialized.
        /// </param>
        /// <param name="stream">
        /// The stream to serialize to.
        /// </param>
        /// <param name="expected">
        /// The expected type.
        /// </param>
        public static void Serialize(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var str = JsonConvert.SerializeObject(obj, expected, _settings);
            SerializationManager.SerializeInner(str, stream, typeof(string));
        }

        /// <summary>
        /// Deserializes a JSON object.
        /// </summary>
        /// <param name="expected">
        /// The expected type.
        /// </param>
        /// <param name="stream">
        /// The stream.
        /// </param>
        /// <returns>
        /// The deserialized object.
        /// </returns>
        public static object Deserialize(Type expected, BinaryTokenStreamReader stream)
        {
            var str = (string)SerializationManager.DeserializeInner(typeof(string), stream);
            return JsonConvert.DeserializeObject(str, expected);
        }

        /// <summary>
        /// Registers this class with the <see cref="SerializationManager"/>.
        /// </summary>
        public static void Register()
        {
            foreach (
                var type in
                    new[]
                    {
                        typeof(JObject), typeof(JArray), typeof(JToken), typeof(JValue), typeof(JProperty),
                        typeof(JConstructor), 
                    })
            {
                SerializationManager.Register(type, DeepCopier, Serialize, Deserialize);
            }
        }
    }
}
