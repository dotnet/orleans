using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BenchmarkGrains;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using UnitTests.GrainInterfaces;


namespace UnitTests.General
{
    /// <summary>
    /// Summary description for GrainReferenceTest
    /// </summary>
    [TestClass]
    public class GrainReferenceTest : UnitTestBase
    {
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestCleanup]
        public void TestCleanup()
        {
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GrainReference")]
        public void GrainReferenceComparison_DifferentReference()
        {
            ISimpleGrain ref1 = TestConstants.GetSimpleGrain();
            ISimpleGrain ref2 = TestConstants.GetSimpleGrain();
            Assert.IsTrue(ref1 != ref2);
            Assert.IsTrue(ref2 != ref1);
            Assert.IsFalse(ref1 == ref2);
            Assert.IsFalse(ref2 == ref1);
            Assert.IsFalse(ref1.Equals(ref2));
            Assert.IsFalse(ref2.Equals(ref1));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("AsynchronyPrimitives")]
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GrainReference")]
        public void GrainReference_Pass_this()
        {
            IChainedGrain g1 = ChainedGrainFactory.GetGrain(1);
            IChainedGrain g2 = ChainedGrainFactory.GetGrain(2);
            
            g1.PassThis(g2).Wait();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization"), TestCategory("GrainReference")]
        public void GrainReference_DotNet_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization"), TestCategory("GrainReference")]
        public void GrainReference_DotNet_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        // Test case currently fails:
        // Json serializer requires message types to be simple DTOs with default constuctors and read-write properties
        // http://stackoverflow.com/questions/19517422/eastnetq-json-serialization-exception
        // Newtonsoft.Json.JsonSerializationException: Unable to find a constructor to use for type SimpleGrainFactory+SimpleGrainReference. A class should either have a default constructor, one constructor with arguments or a constructor marked with the JsonConstructor attribute. Path 'A', line 1, position 5.
        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization"), TestCategory("Json"), TestCategory("GrainReference")]
        public void GrainReference_Json_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, true, true);
        }

        // Test case currently fails:
        // Json serializer requires message types to be simple DTOs with default constuctors and read-write properties
        // http://stackoverflow.com/questions/19517422/eastnetq-json-serialization-exception
        // Newtonsoft.Json.JsonSerializationException: Unable to find a constructor to use for type SimpleGrainFactory+SimpleGrainReference. A class should either have a default constructor, one constructor with arguments or a constructor marked with the JsonConstructor attribute. Path 'A', line 1, position 5.
        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization"), TestCategory("Json"), TestCategory("GrainReference")]
        public void GrainReference_Json_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, true);
        }

        private static void TestGrainReferenceSerialization(int id, bool resolveBeforeSerialize, bool useJson)
        {
            // Make sure grain references serialize well through .NET serializer.
            var grain = TestConstants.GetSimpleGrain();

            if (resolveBeforeSerialize)
            {
                grain.SetA(id).Wait(); //  Resolve GR
            }

            object other;
            if (useJson)
            {
                // Serialize + Deserialise through Json serializer
                other = NewtonsoftJsonSerialiseRoundtrip(grain);
                //other = JavaScriptJsonSerialiseRoundtrip(grain);
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

        private static object JavaScriptJsonSerialiseRoundtrip(object obj)
        {
            JavaScriptSerializer jsonSerializer = new JavaScriptSerializer();
            string json = jsonSerializer.Serialize(obj);
            object other = jsonSerializer.Deserialize(json, obj.GetType());
            return other;
        }

#if DEBUG || REVISIT
        [TestMethod, TestCategory("Failures"), TestCategory("Serialization"), TestCategory("GrainReference"), TestCategory("Interner")]
        public void GrainReference_Interning()
        {
            Guid guid = new Guid();
            GrainId grainId = GrainId.GetGrainIdForTesting(guid);
            GrainReference g1 = GrainReference.FromGrainId(grainId);
            GrainReference g2 = GrainReference.FromGrainId(grainId);
            Assert.AreEqual(g1, g2, "Should be equal GrainReference's");
            Assert.AreSame(g1, g2, "Should be same / intern'ed GrainReference object");

            // Round-trip through Serializer
            GrainReference g3 = (GrainReference) SerializationManager.RoundTripSerializationForTesting(g1);
            Assert.AreEqual(g3, g1, "Should be equal GrainReference's");
            Assert.AreEqual(g3, g2, "Should be equal GrainReference's");
            Assert.AreSame(g3, g1, "Should be same / intern'ed GrainReference object");
            Assert.AreSame(g3, g2, "Should be same / intern'ed GrainReference object");
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Serialization"), TestCategory("GrainReference"), TestCategory("Interner")]
        public void GrainReference_Interning_Sys_DirectoryGrain()
        {
            GrainReference g1 = GrainReference.FromGrainId(Constants.DirectoryServiceId);
            GrainReference g2 = GrainReference.FromGrainId(Constants.DirectoryServiceId);
            Assert.AreEqual(g1, g2, "Should be equal GrainReference's");
            Assert.AreSame(g1, g2, "Should be same / intern'ed GrainReference object");

            // Round-trip through Serializer
            GrainReference g3 = (GrainReference) SerializationManager.RoundTripSerializationForTesting(g1);
            Assert.AreEqual(g3, g1, "Should be equal GrainReference's");
            Assert.AreEqual(g3, g2, "Should be equal GrainReference's");
            Assert.AreSame(g3, g1, "Should be same / intern'ed GrainReference object");
            Assert.AreSame(g3, g2, "Should be same / intern'ed GrainReference object");
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Serialization"), TestCategory("GrainReference"), TestCategory("Interner")]
        public void GrainReference_Interning_Sys_StoreGrain()
        {
            GrainReference g1 = (GrainReference) MemoryStorageGrainFactory.GetGrain(0);
            GrainReference g2 = (GrainReference) MemoryStorageGrainFactory.GetGrain(0);
            Assert.AreEqual(g1, g2, "Should be equal GrainReference's");
            Assert.AreSame(g1, g2, "Should be same / intern'ed GrainReference object");

            // Round-trip through Serializer
            GrainReference g3 = (GrainReference) SerializationManager.RoundTripSerializationForTesting(g1);
            Assert.AreEqual(g3, g1, "Should be equal GrainReference's");
            Assert.AreEqual(g3, g2, "Should be equal GrainReference's");
            Assert.AreSame(g3, g1, "Should be same / intern'ed GrainReference object");
            Assert.AreSame(g3, g2, "Should be same / intern'ed GrainReference object");
        }
#endif
    }
}
