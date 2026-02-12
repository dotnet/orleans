using Newtonsoft.Json;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public interface IFooGrain : IGrain { }

    [GrainType("foo`1")]
    [StatelessWorker]
    public class FooGrain : Grain, IFooGrain { }

    /// <summary>
    /// Tests for Orleans grain reference functionality.
    /// Validates grain reference equality, comparison, serialization (including JSON),
    /// passing grain references between grains, handling null references,
    /// and proper hash code generation for distributed systems.
    /// </summary>
    [TestCategory("BVT"), TestCategory("GrainReference")]
    public class GrainReferenceTest : HostedTestClusterEnsureDefaultStarted
    {
        public GrainReferenceTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that grain references produce consistent uniform hash codes.
        /// Validates that the hash code generation is deterministic and stable,
        /// which is crucial for consistent grain placement and routing in the cluster.
        /// </summary>
        [Fact]
        public void GrainReferenceComparison_ShouldProduceUniformHashCode()
        {
            var simpleGrain = this.GrainFactory.GetGrain<ISimpleGrain>(1234L, UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);
            var r = simpleGrain as GrainReference;
            Assert.NotNull(r);

            // Hey there stranger. So the test failed here?
            // It's probably because the way hash codes are generated for the GrainReference
            // have changed. If you are sure the new code is repeatable, then it's fine to
            // update the expected value here. Good luck, friend.
            Assert.Equal(3643965955u, r.GetUniformHashCode());
        }

        /// <summary>
        /// Tests grain reference equality and comparison operators.
        /// Validates that different grain references are properly distinguished
        /// using equality operators, Equals method, and inequality checks.
        /// </summary>
        [Fact]
        public void GrainReferenceComparison_DifferentReference()
        {
            ISimpleGrain ref1 = this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);
            ISimpleGrain ref2 = this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);
            Assert.True(ref1 != ref2);
            Assert.True(ref2 != ref1);
            Assert.False(ref1 == ref2);
            Assert.False(ref2 == ref1);
            Assert.False(ref1.Equals(ref2));
            Assert.False(ref2.Equals(ref1));
        }

        /// <summary>
        /// Tests passing grain's own reference (this) to another grain.
        /// Validates that grains can pass references to themselves as method parameters,
        /// enabling callback patterns and grain interconnections.
        /// </summary>
        [Fact]
        public async Task GrainReference_Pass_this()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());

            await g1.PassThis(g2);
        }

        /// <summary>
        /// Tests passing grain references within nested data structures.
        /// Validates that grain references embedded in complex objects are properly
        /// serialized and maintain their identity when passed between grains.
        /// </summary>
        [Fact]
        public async Task GrainReference_Pass_this_Nested()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());

            await g1.PassThisNested(new ChainGrainHolder { Next = g2 });
        }

        /// <summary>
        /// Tests passing null grain references between grains.
        /// Validates that null references are properly handled in both direct
        /// parameter passing and when embedded in data structures.
        /// </summary>
        [Fact]
        public async Task GrainReference_Pass_Null()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());

            // g1 will pass a null reference to g2
            await g1.PassNullNested(new ChainGrainHolder { Next = g2 });
            Assert.Null(await g2.GetNext());
            await g1.PassNull(g2);
            Assert.Null(await g2.GetNext());
        }

        /// <summary>
        /// Tests JSON serialization of grain references.
        /// Validates that grain references can be serialized to JSON and deserialized
        /// back while maintaining their identity and functionality.
        /// </summary>
        [Fact, TestCategory("Serialization"), TestCategory("JSON")]
        public void GrainReference_Json_Serialization()
        {
            int id = Random.Shared.Next();
            TestGrainReferenceSerialization(id, true);
        }

        /// <summary>
        /// Tests JSON serialization of grain references within complex objects.
        /// Validates that grain references nested in data structures are properly
        /// serialized/deserialized and remain functional after round-trip.
        /// </summary>
        [Fact, TestCategory("Serialization"), TestCategory("JSON")]
        public async Task GrainReference_Json_Serialization_Nested()
        {
            var settings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(this.HostedCluster.Client.ServiceProvider);

            var grain = HostedCluster.GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId());
            await grain.SetA(56820);
            var input = new GenericGrainReferenceHolder
            {
                Reference = grain as GrainReference
            };

            var json = JsonConvert.SerializeObject(input, settings);
            var output = JsonConvert.DeserializeObject<GenericGrainReferenceHolder>(json, settings);

            Assert.Equal(input.Reference, output.Reference);
            var reference = output.Reference;
            Assert.Equal(56820, await ((ISimpleGrain)reference).GetA());
        }

        [Serializable]
        [GenerateSerializer]
        public class GenericGrainReferenceHolder
        {
            [JsonProperty]
            [Id(0)]
            public GrainReference Reference { get; set; }
        }

        /// <summary>
        /// Tests JSON serialization of unresolved grain references.
        /// Validates that grain references can be serialized even before they are
        /// resolved (activated), maintaining lazy resolution semantics.
        /// </summary>
        [Fact, TestCategory("Serialization"), TestCategory("JSON")]
        public void GrainReference_Json_Serialization_Unresolved()
        {
            int id = Random.Shared.Next();
            TestGrainReferenceSerialization(id, false);
        }

        /// <summary>
        /// Tests grain reference interning for system grains.
        /// Validates that multiple requests for the same grain return the same
        /// reference object (interning), optimizing memory usage.
        /// Note: Currently skipped as grain reference interning is not implemented.
        /// </summary>
        [Fact(Skip = "GrainReference interning is not currently implemented."), TestCategory("Serialization"), TestCategory("Interner")]
        public void GrainReference_Interning_Sys_StoreGrain()
        {
            var g1 = (GrainReference)this.GrainFactory.GetGrain<IMemoryStorageGrain>(0);
            var g2 = (GrainReference)this.GrainFactory.GetGrain<IMemoryStorageGrain>(0);
            Assert.Equal(g1, g2); // Should be equal GrainReferences.
            Assert.Same(g1, g2); // Should be same / interned GrainReference object

            // Round-trip through Serializer
            var g3 = this.HostedCluster.RoundTripSerializationForTesting(g1);
            Assert.Equal(g3, g1);
            Assert.Equal(g3, g2);
            Assert.Same(g3, g1);
            Assert.Same(g3, g2);
        }

        private void TestGrainReferenceSerialization(int id, bool resolveBeforeSerialize)
        {
            // Make sure grain references serialize well through .NET serializer.
            var grain = this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);

            if (resolveBeforeSerialize)
            {
                grain.SetA(id).Wait(); //  Resolve GR
            }

            // Serialize + Deserialize through Json serializer
            var other = NewtonsoftJsonSerializeRoundtrip(grain);

            if (!resolveBeforeSerialize)
            {
                grain.SetA(id).Wait(); //  Resolve GR
            }

            Assert.IsAssignableFrom(grain.GetType(), other);
            Assert.NotNull(other);
            Assert.Equal(grain,  other);  // "Deserialized grain reference equality is preserved"
            int res = other.GetA().Result;
            Assert.Equal(id,  res);  // "Returned values from call to deserialized grain reference"
        }

        private T NewtonsoftJsonSerializeRoundtrip<T>(T obj)
        {
            var settings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(this.HostedCluster.Client.ServiceProvider);
            // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
            string json = JsonConvert.SerializeObject(obj, settings);
            object other = JsonConvert.DeserializeObject(json, typeof(T), settings);
            return (T)other;
        }
    }
}
