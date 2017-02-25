using Orleans.Serialization;
using Xunit;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using System.Collections;
using TestExtensions;

namespace Tester.SerializationTests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class SerializationTestsImmutableCollections
    {
        private readonly TestEnvironmentFixture fixture;

        public SerializationTestsImmutableCollections(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        void RoundTripCollectionSerializationTest<T>(IEnumerable<T> input)
        {
            var output = this.fixture.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.Equal<T>(input,output);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_Dictionary()
        {
            var original = ImmutableDictionary.CreateBuilder<string, string>();
            original.Add("a","b");
            original.Add("c","d");
            var dict = original.ToImmutable();
            
            RoundTripCollectionSerializationTest(dict);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_Array()
        {
            var original = ImmutableArray.Create("1","2","3");
            RoundTripCollectionSerializationTest(original);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_HashSet()
        {
            var original = ImmutableHashSet.Create("1", "2", "3");
            RoundTripCollectionSerializationTest(original);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_List()
        {
            var original = ImmutableList.Create("1", "2", "3");
            RoundTripCollectionSerializationTest(original);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_Queue()
        {
            var original = ImmutableQueue.Create("1", "2", "3");
            RoundTripCollectionSerializationTest(original);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_SortedSet()
        {
            var original = ImmutableSortedSet.Create("1", "2", "3");
            RoundTripCollectionSerializationTest(original);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ImmutableCollections"), TestCategory("Serialization")]
        public void SerializationTests_ImmutableCollections_SortedDictionary()
        {
            var original = ImmutableSortedDictionary.CreateBuilder<string, string>();
            original.Add("a", "b");
            original.Add("c", "d");
            var dict = original.ToImmutable();

            RoundTripCollectionSerializationTest(dict);
        }
    }
}
