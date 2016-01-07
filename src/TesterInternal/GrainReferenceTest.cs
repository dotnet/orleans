using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for GrainReferenceTest
    /// </summary>
    [TestClass]
    public class GrainReferenceTest : UnitTestSiloHost
    {
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            //ResetDefaultRuntimes();
        }

        [TestCleanup]
        public void TestCleanup()
        {
        }

        [TestMethod, TestCategory("Functional"), TestCategory("GrainReference")]
        public void GrainReferenceComparison_DifferentReference()
        {
            ISimpleGrain ref1 = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), Grains.SimpleGrain.SimpleGrainNamePrefix);
            ISimpleGrain ref2 = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), Grains.SimpleGrain.SimpleGrainNamePrefix);
            Assert.IsTrue(ref1 != ref2);
            Assert.IsTrue(ref2 != ref1);
            Assert.IsFalse(ref1 == ref2);
            Assert.IsFalse(ref2 == ref1);
            Assert.IsFalse(ref1.Equals(ref2));
            Assert.IsFalse(ref2.Equals(ref1));
        }

        [TestMethod,TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void TaskCompletionSource_Resolve()
        {
            string str = "Hello TaskCompletionSource";
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            Task task = tcs.Task;
            Assert.IsFalse(task.IsCompleted, "TCS.Task not yet completed");
            tcs.SetResult(str);
            Assert.IsTrue(task.IsCompleted, "TCS.Task is now completed");
            Assert.IsFalse(task.IsFaulted, "TCS.Task should not be in faulted state: " + task.Exception);
            Assert.AreEqual(str, tcs.Task.Result, "Result");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("GrainReference")]
        public void GrainReference_Pass_this()
        {
            IChainedGrain g1 = GrainClient.GrainFactory.GetGrain<IChainedGrain>(1);
            IChainedGrain g2 = GrainClient.GrainFactory.GetGrain<IChainedGrain>(2);
            
            g1.PassThis(g2).Wait();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization"), TestCategory("GrainReference")]
        public void GrainReference_DotNet_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization"), TestCategory("GrainReference")]
        public void GrainReference_DotNet_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON"), TestCategory("GrainReference")]
        public void GrainReference_Json_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, true, true);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization"), TestCategory("JSON"), TestCategory("GrainReference")]
        public void GrainReference_Json_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, true);
        }

        private static void TestGrainReferenceSerialization(int id, bool resolveBeforeSerialize, bool useJson)
        {
            // Make sure grain references serialize well through .NET serializer.
            var grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), Grains.SimpleGrain.SimpleGrainNamePrefix);

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

            Assert.IsInstanceOfType(other, grain.GetType(), "Deserialized grain reference type = {0}", grain.GetType());
            ISimpleGrain otherGrain = other as ISimpleGrain;
            Assert.IsNotNull(otherGrain, "Other grain");
            Assert.AreEqual(grain, otherGrain, "Deserialized grain reference equality is preserved");
            int res = otherGrain.GetA().Result;
            Assert.AreEqual(id, res, "Returned values from call to deserialized grain reference");
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
