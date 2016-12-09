﻿using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using System;
using Newtonsoft.Json;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Summary description for JsonGrainTests
    /// </summary>
    public class JsonGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("JSON"), TestCategory("GetGrain")]
        public async Task JSON_GetGrain()
        {
            int id = random.Next();
            var grain = GrainClient.GrainFactory.GetGrain<IJsonEchoGrain>(id);
            await grain.Ping();
        }

        [Fact, TestCategory("BVT"), TestCategory("JSON"), TestCategory("Echo")]
        public async Task JSON_EchoJson()
        {
            int id = random.Next();
            var grain = GrainClient.GrainFactory.GetGrain<IJsonEchoGrain>(id);

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

        [RegisterSerializer]
        public class JObjectSerializationExample1
        {
            static JObjectSerializationExample1()
            {
                Register();
            }

            public static object DeepCopier(object original)
            {
                // I assume JObject is immutable, so no need to deep copy.
                // Alternatively, can copy via JObject.ToString and JObject.Parse().
                return original;
            }

            public static void Serializer(object untypedInput, BinaryTokenStreamWriter stream, Type expected)
            {
                var input = (JObject)(untypedInput);
                string str = input.ToString();
                SerializationManager.Serialize(str, stream);
            }

            public static object Deserializer(Type expected, BinaryTokenStreamReader stream)
            {
                var str = (string)(SerializationManager.Deserialize(typeof(string), stream));
                return JObject.Parse(str);
            }

            public static void Register()
            {
                SerializationManager.Register(typeof(JObject), DeepCopier, Serializer, Deserializer);
            }
        }
    }
}
