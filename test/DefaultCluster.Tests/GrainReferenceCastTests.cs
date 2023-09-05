using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests
{
    using Microsoft.Extensions.DependencyInjection;

    public class GrainReferenceCastTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly IInternalGrainFactory internalGrainFactory;

        public GrainReferenceCastTests(DefaultClusterFixture fixture) : base(fixture)
        {
            var client = this.HostedCluster.Client;
            this.internalGrainFactory = client.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyType()
        {
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), SimpleGrain.SimpleGrainNamePrefix);
            GrainReference cast = (GrainReference)grain.AsReference<ISimpleGrain>();
            Assert.IsAssignableFrom(grain.GetType(), cast);
            Assert.IsAssignableFrom<ISimpleGrain>(cast);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyTypePolymorphic()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter
            IAddressable grain = this.GrainFactory.GetGrain<IMultifacetTestGrain>(0);
            Assert.IsAssignableFrom<IMultifacetWriter>(grain);
            Assert.IsAssignableFrom<IMultifacetReader>(grain);

            IAddressable cast = grain.AsReference<IMultifacetReader>();
            Assert.IsAssignableFrom(grain.GetType(), cast);
            Assert.IsAssignableFrom<IMultifacetWriter>(cast);
            Assert.IsAssignableFrom<IMultifacetReader>(grain);

            IAddressable cast2 = grain.AsReference<IMultifacetWriter>();
            Assert.IsAssignableFrom(grain.GetType(), cast2);
            Assert.IsAssignableFrom<IMultifacetReader>(cast2);
            Assert.IsAssignableFrom<IMultifacetWriter>(grain);
        }

        // Test case currently fails intermittently
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastMultifacetRWReference()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter
            int newValue = 3;

            IMultifacetWriter writer = this.GrainFactory.GetGrain<IMultifacetWriter>(1);
            // No Wait in this test case

            IMultifacetReader reader = writer.AsReference<IMultifacetReader>();  // --> Test case intermittently fails here
            // Error: System.InvalidCastException: Grain reference MultifacetGrain.MultifacetWriterFactory+MultifacetWriterReference service interface mismatch: expected interface id=[1947430462] received interface name=[MultifacetGrain.IMultifacetWriter] id=[62435819] in grainRef=[GrainReference:*std/b198f19f]

            writer.SetValue(newValue).Wait();

            Task<int> readAsync = reader.GetValue();
            readAsync.Wait();
            int result = readAsync.Result;

            Assert.Equal(newValue, result);
        }

        // Test case currently fails
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastMultifacetRWReferenceWaitForResolve()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter

            //Interface Id values for debug:
            // IMultifacetWriter = 62435819
            // IMultifacetReader = 1947430462
            // IMultifacetTestGrain = 222717230 (also compatable with 1947430462 or 62435819)

            int newValue = 4;

            IMultifacetWriter writer = this.GrainFactory.GetGrain<IMultifacetWriter>(2);
            
            IMultifacetReader reader = writer.AsReference<IMultifacetReader>(); // --> Test case fails here
            // Error: System.InvalidCastException: Grain reference MultifacetGrain.MultifacetWriterFactory+MultifacetWriterReference service interface mismatch: expected interface id=[1947430462] received interface name=[MultifacetGrain.IMultifacetWriter] id=[62435819] in grainRef=[GrainReference:*std/8408c2bc]
            
            writer.SetValue(newValue).Wait();

            int result = reader.GetValue().Result;

            Assert.Equal(newValue, result);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastFailInternalCastFromBadType()
        {
            var grain = this.GrainFactory.GetGrain<ISimpleGrain>(
                Random.Shared.Next(),
                SimpleGrain.SimpleGrainNamePrefix);

            // Attempting to cast a grain to a non-grain type should fail.
            Assert.Throws<ArgumentException>(() => this.internalGrainFactory.Cast(grain, typeof(bool)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastInternalCastFromMyType()
        {
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), SimpleGrain.SimpleGrainNamePrefix);
            
            // This cast should be a no-op, since the interface matches the initial reference's exactly.
            IAddressable cast = grain.Cast<ISimpleGrain>();

            Assert.Same(grain, cast);
            Assert.IsAssignableFrom<ISimpleGrain>(cast);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastInternalCastUpFromChild()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            
            // This cast should be a no-op, since the interface is implemented by the initial reference's interface.
            IAddressable cast = grain.Cast<IGeneratorTestGrain>();

            Assert.Same(grain, cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromChild()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            GrainReference cast = (GrainReference) grain.AsReference<IGeneratorTestGrain>();
            Assert.IsAssignableFrom<IGeneratorTestDerivedGrain1>(cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task FailSideCastAfterResolve()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            Assert.True(grain.StringIsNullOrEmpty().Result);

            // Fails the next line as grain reference is already resolved
            IGeneratorTestDerivedGrain2 cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
            
            await Assert.ThrowsAsync<InvalidCastException>(() => cast.StringConcat("a", "b", "c"));
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task FailOperationAfterSideCast()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());

            // Cast works optimistically when the grain reference is not already resolved
            IGeneratorTestDerivedGrain2 cast = grain.AsReference<IGeneratorTestDerivedGrain2>();

            // Operation fails when grain reference is completely resolved
            await Assert.ThrowsAsync<InvalidCastException>(() => cast.StringConcat("a", "b", "c"));
        }


        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void FailSideCastAfterContinueWith()
        {
            Assert.Throws<InvalidCastException>(() =>
            {
                // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
                // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
                try
                {
                    IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
                    IGeneratorTestDerivedGrain2 cast = null;
                    Task<bool> av = grain.StringIsNullOrEmpty();
                    Task<bool> av2 = av.ContinueWith((Task<bool> t) => Assert.True(t.Result)).ContinueWith((_AppDomain) =>
                    {
                        cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
                    }).ContinueWith((_) => cast.StringConcat("a", "b", "c")).ContinueWith((_) => cast.StringIsNullOrEmpty().Result);
                    Assert.False(av2.Result);
                }
                catch (AggregateException ae)
                {
                    Exception ex = ae.InnerException;
                    while (ex is AggregateException) ex = ex.InnerException;
                    throw ex;
                }
                Assert.True(false, "Exception should have been raised");
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            GrainReference cast;
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
  
            // Parent
            cast = (GrainReference) grain.AsReference<IGeneratorTestDerivedGrain2>();
            Assert.IsAssignableFrom<IGeneratorTestDerivedDerivedGrain>(cast);
            Assert.IsAssignableFrom<IGeneratorTestDerivedGrain2>(cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
            
            // Cross-cast outside the inheritance hierarchy should not work
            Assert.False(cast is IGeneratorTestDerivedGrain1);

            // Grandparent
            cast = (GrainReference) grain.AsReference<IGeneratorTestGrain>();
            Assert.IsAssignableFrom<IGeneratorTestDerivedDerivedGrain>(cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);

            // Cross-cast outside the inheritance hierarchy should not work
            Assert.False(cast is IGeneratorTestDerivedGrain1);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromDerivedDerivedChild()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) this.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
            GrainReference cast = (GrainReference) grain.AsReference<IGeneratorTestDerivedGrain2>();
            Assert.IsAssignableFrom<IGeneratorTestDerivedDerivedGrain>(cast);
            Assert.IsAssignableFrom<IGeneratorTestDerivedGrain2>(cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
            Assert.False(cast is IGeneratorTestDerivedGrain1);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastAsyncGrainRefCastFromSelf()
        {
            IAddressable grain = this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), SimpleGrain.SimpleGrainNamePrefix);
            ISimpleGrain cast = grain.AsReference<ISimpleGrain>();

            Task<int> successfulCallPromise = cast.GetA();
            successfulCallPromise.Wait();
            Assert.Equal(TaskStatus.RanToCompletion, successfulCallPromise.Status);
        }


        // todo: implement white box access
#if TODO
        [Fact]
        public void CastAsyncGrainRefUpCastFromChild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestDerivedGrain1Reference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "",
                "GeneratorTestGrain.GeneratorTestDerivedGrain1" );
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestGrainFactory.Cast(grain);
            Assert.NotNull(cast);
            //Assert.Same(typeof(IGeneratorTestGrain), cast.GetType());

            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.True(cast.IsResolved);
            Assert.True(grain.IsResolved);
        }

        [Fact]
        public void CastAsyncGrainRefUpCastFromGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestDerivedDerivedGrainReference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestDerivedDerivedGrain"
            );
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestGrainFactory.Cast(grain);
            Assert.NotNull(cast);
            //Assert.Same(typeof(IGeneratorTestGrain), cast.GetType());

            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.True(cast.IsResolved);
            Assert.True(grain.IsResolved);
        }

        [Fact]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastAsyncGrainRefFailSideCastToPeer()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestDerivedGrain1Reference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestDerivedGrain1"
            );
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestDerivedGrain2Factory.Cast(grain);
            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.True(false, "Exception should have been raised");
        }

        [Fact]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastAsyncGrainRefFailDownCastToChild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestGrainReference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestGrain");
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestDerivedGrain1Factory.Cast(grain);
            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.True(false, "Exception should have been raised");
        }

        [Fact]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastAsyncGrainRefFailDownCastToGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestGrainReference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestGrain");
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestDerivedDerivedGrainFactory.Cast(grain);
            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.True(false, "Exception should have been raised");
        }
#endif
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastCallMethodInheritedFromBaseClass()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            Task<bool> isNullStr;

            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            isNullStr = grain.StringIsNullOrEmpty();
            Assert.True(isNullStr.Result, "Value should be null initially");

            isNullStr = grain.StringSet("a").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.False(isNullStr.Result, "Value should not be null after SetString(a)");

            isNullStr = grain.StringSet(null).ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.True(isNullStr.Result, "Value should be null after SetString(null)");

            IGeneratorTestGrain cast = grain.AsReference<IGeneratorTestGrain>();
            isNullStr = cast.StringSet("b").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.False(isNullStr.Result, "Value should not be null after cast.SetString(b)");
        }
    }
}
