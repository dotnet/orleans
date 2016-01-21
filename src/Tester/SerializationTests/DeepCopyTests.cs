using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Serialization;
using Orleans.UnitTest.GrainInterfaces;
using UnitTests.GrainInterfaces;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the deep copy of built-in and user-defined types
    /// </summary>
    [TestClass]
    public class DeepCopyTests
    {

        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void DeepCopyTests_BuiltinCollections()
        {
            {
                var original = new int[] { 0, 1, 2 };
                var copy = (int[])SerializationManager.DeepCopy(original);
                copy[2] = 0;
                Assert.AreEqual(original[2], 2);
            }
            {
                var original = new int[] { 0, 1, 2 }.ToList();
                var copy = (List<int>)SerializationManager.DeepCopy(original);
                copy[2] = 0;
                Assert.AreEqual(original[2], 2);
            }
            {
                var original = new int[][] { new int[] { 0, 1 }, new int[] { 2, 3 } };
                var copy = (int[][])SerializationManager.DeepCopy(original);
                copy[1][0] = 0;
                Assert.AreEqual(original[1][0], 2);
            }
            {
                var original = new Dictionary<int, int>();
                original[0] = 1;
                original[1] = 2;
                var copy = (Dictionary<int, int>)SerializationManager.DeepCopy(original);
                copy[1] = 0;
                Assert.AreEqual(original[1], 2);
            }
            {
                var original = new Dictionary<string, Dictionary<string, string>>();
                original["a"] = new Dictionary<string, string>();
                original["a"]["0"] = "1";
                original["a"]["1"] = "2";
                var copy = (Dictionary<string, Dictionary<string, string>>)SerializationManager.DeepCopy(original);
                copy["a"]["1"] = "";
                Assert.AreEqual(original["a"]["1"], "2");
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void DeepCopyTests_UserDefinedType()
        {
            {
                var original = new LargeTestData();
                original.SetNumber("a", 1);
                original.SetNumber("b", 2);
                original.SetEnemy(0, CampaignEnemyTestType.Enemy3);
                original.SetBit(19);
                var copy = (LargeTestData)SerializationManager.DeepCopy(original);
                Assert.AreEqual(1, copy.GetNumber("a"));
                Assert.AreEqual(2, copy.GetNumber("b"));
                Assert.AreEqual(CampaignEnemyTestType.Enemy3, copy.GetEnemy(0));
                Assert.AreEqual(true, copy.GetBit(19));
                // change copy
                copy.SetNumber("b", 0);
                copy.SetEnemy(0, CampaignEnemyTestType.Brute);
                copy.SetBit(19, false);
                // original must be unchanged
                Assert.AreEqual(2, original.GetNumber("b"));
                Assert.AreEqual(CampaignEnemyTestType.Enemy3, original.GetEnemy(0));
                Assert.AreEqual(true, original.GetBit(19));
            }
        }

    }
}
