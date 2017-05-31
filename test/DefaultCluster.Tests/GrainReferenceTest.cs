using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Summary description for GrainReferenceTest
    /// </summary>
    [TestCategory("BVT"), TestCategory("Functional"), TestCategory("GrainReference")]
    public class GrainReferenceTest : HostedTestClusterEnsureDefaultStarted
    {
        public GrainReferenceTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact]
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

        [Fact]
        public void GrainReference_Pass_this()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            
            g1.PassThis(g2).Wait();
        }

        [Fact]
        public void GrainReference_Pass_this_Nested()
        {
            IChainedGrain g1 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());
            IChainedGrain g2 = this.GrainFactory.GetGrain<IChainedGrain>(GetRandomGrainId());

            g1.PassThisNested(new ChainGrainHolder { Next = g2 }).Wait();
        }

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

        [Fact, TestCategory("Serialization")]
        public void GrainReference_DotNet_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [Fact, TestCategory("Serialization")]
        public void GrainReference_DotNet_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, false);
        }

        [Fact, TestCategory("Serialization"), TestCategory("JSON")]
        public void GrainReference_Json_Serialization()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, true, true);
        }

        [Fact, TestCategory("Serialization"), TestCategory("JSON")]
        public async Task GrainReference_Json_Serialization_Nested()
        {
            var settings = OrleansJsonSerializer.GetDefaultSerializerSettings(HostedCluster.SerializationManager, HostedCluster.GrainFactory);
            
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
        public class GenericGrainReferenceHolder
        {
            [JsonProperty]
            public GrainReference Reference { get; set; }
        }

        [Fact, TestCategory("Serialization"), TestCategory("JSON")]
        public void GrainReference_Json_Serialization_Unresolved()
        {
            int id = random.Next();
            TestGrainReferenceSerialization(id, false, true);
        }

        [Fact(Skip = "GrainReference interning is not currently implemented."), TestCategory("Serialization"), TestCategory("Interner")]
        public void GrainReference_Interning()
        {
            var grainId = GrainId.GetGrainIdForTesting(new Guid());
            var g1 = GrainReference.FromGrainId(grainId, null);
            var g2 = GrainReference.FromGrainId(grainId, null);
            Assert.Equal(g1, g2); // Should be equal GrainReferences
            Assert.Same(g1, g2); // Should be same / interned GrainReference object

            // Round-trip through Serializer
            var g3 = this.HostedCluster.SerializationManager.RoundTripSerializationForTesting(g1);
            Assert.Equal(g3, g1);
            Assert.Equal(g3, g2);
            Assert.Same(g3, g1);
            Assert.Same(g3, g2);
        }

        [Fact(Skip = "GrainReference interning is not currently implemented."), TestCategory("Serialization"), TestCategory("Interner")]
        public void GrainReference_Interning_Sys_DirectoryGrain()
        {
            var g1 = GrainReference.FromGrainId(Constants.DirectoryServiceId, null);
            var g2 = GrainReference.FromGrainId(Constants.DirectoryServiceId, null);
            Assert.Equal(g1, g2); // Should be equal GrainReferences.
            Assert.Same(g1, g2); // Should be same / interned GrainReference object

            // Round-trip through Serializer
            var g3 = this.HostedCluster.SerializationManager.RoundTripSerializationForTesting(g1);
            Assert.Equal(g3, g1);
            Assert.Equal(g3, g2);
            Assert.Same(g3, g1);
            Assert.Same(g3, g2);
        }

        [Fact(Skip = "GrainReference interning is not currently implemented."), TestCategory("Serialization"), TestCategory("Interner")]
        public void GrainReference_Interning_Sys_StoreGrain()
        {
            var g1 = (GrainReference)this.GrainFactory.GetGrain<IMemoryStorageGrain>(0);
            var g2 = (GrainReference)this.GrainFactory.GetGrain<IMemoryStorageGrain>(0);
            Assert.Equal(g1, g2); // Should be equal GrainReferences.
            Assert.Same(g1, g2); // Should be same / interned GrainReference object

            // Round-trip through Serializer
            var g3 = this.HostedCluster.SerializationManager.RoundTripSerializationForTesting(g1);
            Assert.Equal(g3, g1);
            Assert.Equal(g3, g2);
            Assert.Same(g3, g1);
            Assert.Same(g3, g2);
        }

        private void TestGrainReferenceSerialization(int id, bool resolveBeforeSerialize, bool useJson)
        {
            // Make sure grain references serialize well through .NET serializer.
            var grain = this.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);

            if (resolveBeforeSerialize)
            {
                grain.SetA(id).Wait(); //  Resolve GR
            }

            ISimpleGrain other;
            if (useJson)
            {
                // Serialize + Deserialize through Json serializer
                other = NewtonsoftJsonSerializeRoundtrip(grain);
            }
            else
            {
                // Serialize + Deserialize through .NET serializer
                other = DotNetSerializeRoundtrip(grain);
            }

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

        private T DotNetSerializeRoundtrip<T>(T obj)
        {
            T other;
            using (var memoryStream = new MemoryStream())
            {
                var formatter = new BinaryFormatter
                {
#if !NETSTANDARD_TODO
                    Context = new StreamingContext(StreamingContextStates.All, new SerializationContext(this.HostedCluster.SerializationManager))
#endif
                };
                formatter.Serialize(memoryStream, obj);
                memoryStream.Flush();
                memoryStream.Position = 0; // Reset to start
                other = (T)formatter.Deserialize(memoryStream);
#if NETSTANDARD_TODO
                // On .NET Standard, currently we need to manually fixup grain references.
                var otherAsRef = other as Orleans.Runtime.GrainReference;
                if (otherAsRef != null) this.GrainFactory.BindGrainReference(otherAsRef);
#endif
            }
            return other;
        }

        private T NewtonsoftJsonSerializeRoundtrip<T>(T obj)
        {
            var settings = OrleansJsonSerializer.GetDefaultSerializerSettings(this.HostedCluster.SerializationManager, this.GrainFactory);
            // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
            string json = JsonConvert.SerializeObject(obj, settings);
            object other = JsonConvert.DeserializeObject(json, typeof(T), settings);
            return (T)other;
        }
    }
}
