using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the deep copy of built-in and user-defined types
    /// </summary>
    public class DeepCopyTests
    {
        public DeepCopyTests()
        {
            SerializationManager.InitializeForTesting();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void DeepCopyTests_BuiltinCollections()
        {
            {
                var original = new int[] { 0, 1, 2 };
                var copy = (int[])SerializationManager.DeepCopy(original);
                copy[2] = 0;
                Assert.Equal(original[2], 2);
            }
            {
                var original = new int[] { 0, 1, 2 }.ToList();
                var copy = (List<int>)SerializationManager.DeepCopy(original);
                copy[2] = 0;
                Assert.Equal(original[2], 2);
            }
            {
                var original = new int[][] { new int[] { 0, 1 }, new int[] { 2, 3 } };
                var copy = (int[][])SerializationManager.DeepCopy(original);
                copy[1][0] = 0;
                Assert.Equal(original[1][0], 2);
            }
            {
                var original = new Dictionary<int, int>();
                original[0] = 1;
                original[1] = 2;
                var copy = (Dictionary<int, int>)SerializationManager.DeepCopy(original);
                copy[1] = 0;
                Assert.Equal(original[1], 2);
            }
            {
                var original = new Dictionary<string, Dictionary<string, string>>();
                original["a"] = new Dictionary<string, string>();
                original["a"]["0"] = "1";
                original["a"]["1"] = "2";
                var copy = (Dictionary<string, Dictionary<string, string>>)SerializationManager.DeepCopy(original);
                copy["a"]["1"] = "";
                Assert.Equal(original["a"]["1"], "2");
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void DeepCopyTests_ImmutableCollections()
        {
            {
                var original = ImmutableDictionary.CreateBuilder<string, Dictionary<string, string>>();
                original.Add("a", new Dictionary<string, string>() {
                    {"0","1" },
                    {"1","2" }
                });
                var dict = original.ToImmutable();
                var copy = (ImmutableDictionary<string, Dictionary<string, string>>)SerializationManager.DeepCopy(dict);
                Assert.Same(dict, copy);
            }
            {
                var original = ImmutableArray.Create<string>("1", "2", "3");
                var copy = (ImmutableArray<string>)SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = ImmutableHashSet.Create("1", "2", "3");
                var copy = (ImmutableHashSet<string>)SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
            {
                var original = ImmutableList.Create("1", "2", "3");
                var copy = (ImmutableList<string>)SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
            {
                var original = ImmutableQueue.Create("1", "2", "3");
                var copy = (ImmutableQueue<string>)SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
            {
                var original = ImmutableSortedDictionary.CreateBuilder<string, Dictionary<string, string>>();
                original.Add("a", new Dictionary<string, string>() {
                    {"0","1" },
                    {"1","2" }
                });
                var dict = original.ToImmutable();
                var copy = (ImmutableSortedDictionary<string, Dictionary<string, string>>)SerializationManager.DeepCopy(dict);
                Assert.Same(dict, copy);
            }
            {
                var original = ImmutableSortedSet.Create("1", "2", "3");
                var copy = (ImmutableSortedSet<string>)SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void DeepCopyTests_UserDefinedType()
        {
            {
                var original = new LargeTestData();
                original.SetNumber("a", 1);
                original.SetNumber("b", 2);
                original.SetEnemy(0, CampaignEnemyTestType.Enemy3);
                original.SetBit(19);
                var copy = (LargeTestData)SerializationManager.DeepCopy(original);
                Assert.Equal(1, copy.GetNumber("a"));
                Assert.Equal(2, copy.GetNumber("b"));
                Assert.Equal(CampaignEnemyTestType.Enemy3, copy.GetEnemy(0));
                Assert.Equal(true, copy.GetBit(19));
                // change copy
                copy.SetNumber("b", 0);
                copy.SetEnemy(0, CampaignEnemyTestType.Brute);
                copy.SetBit(19, false);
                // original must be unchanged
                Assert.Equal(2, original.GetNumber("b"));
                Assert.Equal(CampaignEnemyTestType.Enemy3, original.GetEnemy(0));
                Assert.Equal(true, original.GetBit(19));
            }
        }

    }
}
