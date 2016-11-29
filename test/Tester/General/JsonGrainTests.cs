﻿using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.General
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
    }
}
