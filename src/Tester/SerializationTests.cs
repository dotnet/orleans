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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Orleans.Serialization;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    [TestClass]
    public class SerializationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_JObject()
        {
            const string json = @"{ 
                    CPU: 'Intel', 
                    Drives: [ 
                            'DVD read/writer', 
                            '500 gigabyte hard drive' 
                           ] 
                   }"; 
 
            JObject input = JObject.Parse(json);
            JObject output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.AreEqual(input.ToString(), output.ToString());
        }

        [Orleans.CodeGeneration.RegisterSerializerAttribute()]
        internal class JObjectSerialization
        {
            static JObjectSerialization()
            {
                Register();
            }

            public static object DeepCopier(object original)
            {
                // I assume JObject is immutable, so no need to deep copy.
                return original;
            }

            public static void Serializer(object untypedInput, BinaryTokenStreamWriter stream, Type expected)
            {
                var input = (JObject)(untypedInput);
                string str = input.ToString();
                SerializationManager.SerializeInner(str, stream, typeof(string));
            }

            public static object Deserializer(Type expected, BinaryTokenStreamReader stream)
            {
                var str = (string)(SerializationManager.DeserializeInner(typeof(string), stream));
                return JObject.Parse(str);
            }

            public static void Register()
            {
                SerializationManager.Register(typeof(JObject), DeepCopier, Serializer, Deserializer);
            }
        }
    }
}
