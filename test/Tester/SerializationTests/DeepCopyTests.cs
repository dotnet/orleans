using Microsoft.FSharp.Collections;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TestExtensions;
using UnitTests.FSharpTypes;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Serialization
{
    /// <summary>
    /// Test the deep copy of built-in and user-defined types
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class DeepCopyTests
    {
        private readonly TestEnvironmentFixture fixture;

        public DeepCopyTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void DeepCopyTests_BuiltinCollections()
        {
            {
                var original = new int[] { 0, 1, 2 };
                var copy = (int[])this.fixture.SerializationManager.DeepCopy(original);
                copy[2] = 0;
                Assert.Equal(2, original[2]);
            }
            {
                var original = new int[] { 0, 1, 2 }.ToList();
                var copy = (List<int>)this.fixture.SerializationManager.DeepCopy(original);
                copy[2] = 0;
                Assert.Equal(2, original[2]);
            }
            {
                var original = new int[][] { new int[] { 0, 1 }, new int[] { 2, 3 } };
                var copy = (int[][])this.fixture.SerializationManager.DeepCopy(original);
                copy[1][0] = 0;
                Assert.Equal(2, original[1][0]);
            }
            {
                var original = new Dictionary<int, int>
                {
                    [0] = 1,
                    [1] = 2
                };
                var copy = (Dictionary<int, int>)this.fixture.SerializationManager.DeepCopy(original);
                copy[1] = 0;
                Assert.Equal(2, original[1]);
            }
            {
                var original = new Dictionary<string, Dictionary<string, string>>
                {
                    ["a"] = new Dictionary<string, string>
                    {
                        ["0"] = "1",
                        ["1"] = "2"
                    },
                    ["a"] =
                    {
                        ["0"] = "1"
                    },
                    ["a"] =
                    {
                        ["1"] = "2"
                    }
                };
                var copy = (Dictionary<string, Dictionary<string, string>>)this.fixture.SerializationManager.DeepCopy(original);
                copy["a"]["1"] = "";
                Assert.Equal("2", original["a"]["1"]);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void DeepCopyTests_ImmutableCollections()
        {
            {
                var original = ImmutableDictionary.CreateBuilder<string, Dictionary<string, string>>();
                original.Add("a", new Dictionary<string, string>() {
                    {"0","1" },
                    {"1","2" }
                });
                var dict = original.ToImmutable();
                var copy = (ImmutableDictionary<string, Dictionary<string, string>>)this.fixture.SerializationManager.DeepCopy(dict);
                Assert.Same(dict, copy);
            }
            {
                var original = ImmutableArray.Create<string>("1", "2", "3");
                var copy = (ImmutableArray<string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = ImmutableHashSet.Create("1", "2", "3");
                var copy = (ImmutableHashSet<string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
            {
                var original = ImmutableList.Create("1", "2", "3");
                var copy = (ImmutableList<string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
            {
                var original = ImmutableQueue.Create("1", "2", "3");
                var copy = (ImmutableQueue<string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
            {
                var original = ImmutableSortedDictionary.CreateBuilder<string, Dictionary<string, string>>();
                original.Add("a", new Dictionary<string, string>() {
                    {"0","1" },
                    {"1","2" }
                });
                var dict = original.ToImmutable();
                var copy = (ImmutableSortedDictionary<string, Dictionary<string, string>>)this.fixture.SerializationManager.DeepCopy(dict);
                Assert.Same(dict, copy);
            }
            {
                var original = ImmutableSortedSet.Create("1", "2", "3");
                var copy = (ImmutableSortedSet<string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Same(original, copy);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void DeepCopyTests_UserDefinedType()
        {
            {
                var original = new LargeTestData();
                original.SetNumber("a", 1);
                original.SetNumber("b", 2);
                original.SetEnemy(0, CampaignEnemyTestType.Enemy3);
                original.SetBit(19);
                var copy = (LargeTestData)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(1, copy.GetNumber("a"));
                Assert.Equal(2, copy.GetNumber("b"));
                Assert.Equal(CampaignEnemyTestType.Enemy3, copy.GetEnemy(0));
                Assert.True(copy.GetBit(19));
                // change copy
                copy.SetNumber("b", 0);
                copy.SetEnemy(0, CampaignEnemyTestType.Brute);
                copy.SetBit(19, false);
                // original must be unchanged
                Assert.Equal(2, original.GetNumber("b"));
                Assert.Equal(CampaignEnemyTestType.Enemy3, original.GetEnemy(0));
                Assert.True(original.GetBit(19));
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void DeepCopyTests_FSharp_Collections()
        {
            // F# list
            {
                var original = FSharpList<int>.Empty;
                var copy = (FSharpList<int>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = ListModule.OfSeq(new List<int> { 0, 1, 2 });
                var copy = (FSharpList<int>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }

            // F# set
            {
                var original = new FSharpSet<int>(new List<int>());
                var copy = (FSharpSet<int>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var elements = new List<int>() { 0, 1, 2 };
                var original = SetModule.OfSeq(elements);
                var copy = (FSharpSet<int>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }

            // F# map
            {
                var original = new FSharpMap<int, string>(new List<Tuple<int, string>>());
                var copy = (FSharpMap<int, string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var elements = new List<Tuple<int, string>>(){
                    new Tuple<int, string>(0, "zero"),
                    new Tuple<int, string>(1, "one")
                };
                var original = MapModule.OfSeq(elements);
                var copy = (FSharpMap<int, string>)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void DeepCopyTests_FSharp_Types()
        {
            // discriminated union case with an array field
            {
                var original = DiscriminatedUnion.nonEmptyArray();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = DiscriminatedUnion.emptyArray();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            // discriminated union case with an F# list field
            {
                var original = DiscriminatedUnion.emptyList();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = DiscriminatedUnion.nonEmptyList();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            // discriminated union case with an F# set field
            {
                var original = DiscriminatedUnion.emptySet();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = DiscriminatedUnion.nonEmptySet();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            // discriminated union case with an F# map field
            {
                var original = DiscriminatedUnion.emptyMap();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
            {
                var original = DiscriminatedUnion.nonEmptyMap();
                var copy = (DiscriminatedUnion)this.fixture.SerializationManager.DeepCopy(original);
                Assert.Equal(original, copy);
            }
        }
    }
}
