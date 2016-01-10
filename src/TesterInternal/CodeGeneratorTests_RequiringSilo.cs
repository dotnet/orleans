using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.CodeGeneration
{
    [TestClass]
    public class CodeGeneratorTests_RequiringSilo : HostedTestClusterEnsureDefaultStarted
    {
        // These test cases create GrainReferences, to we need to be connected to silo for that to work.

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("UniqueKey")]
        public void CodeGen_GrainId_TypeCode()
        {
            var g1Key = GetRandomGrainId();
            ITestGrain g1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(g1Key);
            GrainId id1 = ((GrainReference)g1).GrainId;
            UniqueKey k1 = id1.Key;
            Assert.IsTrue(id1.IsGrain, "GrainReference should be for a grain");
            Assert.AreEqual(UniqueKey.Category.Grain, k1.IdCategory, "GrainId should be for self-managed type");
            Assert.AreEqual(g1Key, k1.PrimaryKeyToLong(), "Encoded primary key should match");
            Assert.AreEqual(1146670029, k1.BaseTypeCode, "Encoded type code data should match");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("UniqueKey"), TestCategory("ActivationCollector")]
        public void CollectionTest_GrainId_TypeCode()
        {
            var g1Key = GetRandomGrainId();
            ICollectionTestGrain g1 = GrainClient.GrainFactory.GetGrain<ICollectionTestGrain>(g1Key);
            GrainId id1 = ((GrainReference)g1).GrainId;
            UniqueKey k1 = id1.Key;
            Console.WriteLine("GrainId={0} UniqueKey={1} PK={2} KeyType={3} IdCategory={4}",
                id1, k1, id1.GetPrimaryKeyLong(), k1.IdCategory, k1.BaseTypeCode);
            Assert.IsTrue(id1.IsGrain, "GrainReference should be for a grain");
            Assert.AreEqual(UniqueKey.Category.Grain, k1.IdCategory, "GrainId should be for self-managed type");
            Assert.AreEqual(g1Key, k1.PrimaryKeyToLong(), "Encoded primary key should match");
            Assert.AreEqual(1381240679, k1.BaseTypeCode, "Encoded type code data should match");
        }
    }

}

// ReSharper restore ConvertToConstant.Local
