/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Tester
{
    [TestFixture]
    public class SerializationTests_InnerTypes
    {
        [SetUp]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [Test, Category("BVT"), Category("Functional"), Category("Serialization")]
        public void InnerTypeSerializationTests_DictionaryTest()
        {
            var original = new RootType();
            var str = JsonConvert.SerializeObject(original);
            var jasonDeser = JsonConvert.DeserializeObject<RootType>(str);
            // The below Assert actualy fails!
            // JsonConvert leaves inner types as strings after Deserialization.
            //Assert.AreEqual(original, jasonDeser);


            var orleansDeser = SerializationManager.RoundTripSerializationForTesting(original);
            // Orleans's SerializationManager deserializes everything correctly!
            Assert.AreEqual(original, orleansDeser);

            Console.WriteLine("Done OK.");
        }
    }

    [Serializable]
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

        public override bool Equals(object obj)
        {
            var actual = obj as RootType;
            if (actual == null)
            {
                return false;
            }
            if (MyDictionary == null) return actual.MyDictionary == null;
            if (actual.MyDictionary == null) return false;

            var set1 = new HashSet<KeyValuePair<string, object>>(MyDictionary);
            var set2 = new HashSet<KeyValuePair<string, object>>(actual.MyDictionary);
            bool ret = set1.SetEquals(set2);
            return ret;
        }
    }

    [Serializable]
    public class InnerType
    {
        public InnerType()
        {
            Id = Guid.NewGuid();
            Something = Id.ToString();
        }
        public Guid Id { get; set; }
        public string Something { get; set; }

        public override bool Equals(object obj)
        {
            var actual = obj as InnerType;
            if (actual == null)
            {
                return false;
            }
            return Id.Equals(actual.Id) && Equals(Something, actual.Something);
        }
    }

    /// <summary>
    /// Provides support for serializing JSON values.
    /// </summary>
    [RegisterSerializer]
    public class JsonSerialization_Example2
    {
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
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
            var str = JsonConvert.SerializeObject(obj, _settings);
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
