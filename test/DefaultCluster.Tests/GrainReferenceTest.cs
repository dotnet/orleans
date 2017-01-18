using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Summary description for GrainReferenceTest
    /// </summary>
    public class GrainReferenceTest : HostedTestClusterEnsureDefaultStarted
    {
        public GrainReferenceTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainReference")]
        public void GrainReferenceComparison_DifferentReference()
        {
            ISimpleGrain ref1 = this.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);
            ISimpleGrain ref2 = this.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);
            Assert.True(ref1 != ref2);
            Assert.True(ref2 != ref1);
            Assert.False(ref1 == ref2);
            Assert.False(ref2 == ref1);
            Assert.False(ref1.Equals(ref2));
            Assert.False(ref2.Equals(ref1));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void TaskCompletionSource_Resolve()
        {
            string str = "Hello TaskCompletionSource";
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            Task task = tcs.Task;
            Assert.False(task.IsCompleted, "TCS.Task not yet completed");
            tcs.SetResult(str);
            Assert.True(task.IsCompleted, "TCS.Task is now completed");
            Assert.False(task.IsFaulted, "TCS.Task should not be in faulted state: " + task.Exception);
            Assert.Equal(str, tcs.Task.Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainReference")]
        public void GrainReference_Pass_this()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            
            g1.PassThis(g2).Wait();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainReference")]
        public void GrainReference_Pass_this_Nested()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());

            g1.PassThisNested(new ChainGrainHolder { Next = g2 }).Wait();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("GrainReference")]
        public void GrainReference_DotNet_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("GrainReference")]
        public void GrainReference_DotNet_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON"), TestCategory("GrainReference")]
        public void GrainReference_Json_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, true, true);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON"), TestCategory("GrainReference")]
        public void GrainReference_Json_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, true);
        }

        private static void TestGrainReferenceSerialization(int id, bool resolveBeforeSerialize, bool useJson)
        {
            // Make sure grain references serialize well through .NET serializer.
            var grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);

            if (resolveBeforeSerialize)
            {
                grain.SetA(id).Wait(); //  Resolve GR
            }

            object other;
            if (useJson)
            {
                // Serialize + Deserialise through Json serializer
                other = NewtonsoftJsonSerialiseRoundtrip(grain);
            }
            else
            {
                // Serialize + Deserialise through .NET serializer
                other = DotNetSerialiseRoundtrip(grain);
            }

            if (!resolveBeforeSerialize)
            {
                grain.SetA(id).Wait(); //  Resolve GR
            }

            Assert.IsAssignableFrom(grain.GetType(), other);
            ISimpleGrain otherGrain = other as ISimpleGrain;
            Assert.NotNull(otherGrain);
            Assert.Equal(grain,  otherGrain);  // "Deserialized grain reference equality is preserved"
            int res = otherGrain.GetA().Result;
            Assert.Equal(id,  res);  // "Returned values from call to deserialized grain reference"
        }

        private static object DotNetSerialiseRoundtrip(object obj)
        {
            object other;
            using (var memoryStream = new MemoryStream())
            {
                var formatter = new BinaryFormatter();
                formatter.Serialize(memoryStream, obj);
                memoryStream.Flush();
                memoryStream.Position = 0; // Reset to start
                other = formatter.Deserialize(memoryStream);
            }
            return other;
        }

        private static object NewtonsoftJsonSerialiseRoundtrip(object obj)
        {
            // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(obj);
            object other = Newtonsoft.Json.JsonConvert.DeserializeObject(json, obj.GetType());
            return other;
        }
    }
}
