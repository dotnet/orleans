using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests
{
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>
    /// Tests for Orleans' grain reference casting capabilities.
    /// Validates that grain references can be safely cast between compatible interfaces,
    /// including upcasting to base interfaces, casting between multiple implemented interfaces,
    /// and proper error handling for invalid casts. Tests both pre- and post-activation scenarios.
    /// </summary>
    public class GrainReferenceCastTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly IInternalGrainFactory internalGrainFactory;

        public GrainReferenceCastTests(DefaultClusterFixture fixture) : base(fixture)
        {
            var client = this.HostedCluster.Client;
            this.internalGrainFactory = client.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
        }

        /// <summary>
        /// Tests basic grain reference casting to the same interface type.
        /// Validates that casting a grain reference to its own interface type works correctly
        /// and returns a reference that is assignable to the expected type.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyType()
        {
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), SimpleGrain.SimpleGrainNamePrefix);
            GrainReference cast = (GrainReference)grain.AsReference<ISimpleGrain>();
            Assert.IsAssignableFrom(grain.GetType(), cast);
            Assert.IsAssignableFrom<ISimpleGrain>(cast);
        }

        /// <summary>
        /// Tests casting between multiple interfaces implemented by the same grain.
        /// Validates that a grain implementing multiple interfaces can be cast between
        /// those interfaces while maintaining proper type relationships.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyTypePolymorphic()
        {
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

        /// <summary>
        /// Tests casting between reader and writer interfaces on a multifaceted grain.
        /// Validates that grains implementing multiple related interfaces can be accessed
        /// through different interface views while maintaining state consistency.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task CastMultifacetRWReference()
        {
            int newValue = 3;

            IMultifacetWriter writer = this.GrainFactory.GetGrain<IMultifacetWriter>(1);
            // No Wait in this test case

            IMultifacetReader reader = writer.AsReference<IMultifacetReader>();

            await writer.SetValue(newValue);

            Task<int> readAsync = reader.GetValue();
            int result = await readAsync;

            Assert.Equal(newValue, result);
        }

        /// <summary>
        /// Tests casting between interfaces with explicit grain resolution.
        /// Validates interface casting behavior when the grain reference is resolved
        /// before casting, ensuring proper interface compatibility checks.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task CastMultifacetRWReferenceWaitForResolve()
        {
            int newValue = 4;

            IMultifacetWriter writer = this.GrainFactory.GetGrain<IMultifacetWriter>(2);
            
            IMultifacetReader reader = writer.AsReference<IMultifacetReader>();
            
            await writer.SetValue(newValue);

            int result = await reader.GetValue();

            Assert.Equal(newValue, result);
        }

        /// <summary>
        /// Tests that invalid cast attempts throw appropriate exceptions.
        /// Validates that attempting to cast a grain reference to a non-grain type
        /// (like bool) results in an ArgumentException.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastFailInternalCastFromBadType()
        {
            var grain = this.GrainFactory.GetGrain<ISimpleGrain>(
                Random.Shared.Next(),
                SimpleGrain.SimpleGrainNamePrefix);

            // Attempting to cast a grain to a non-grain type should fail.
            Assert.Throws<ArgumentException>(() => this.internalGrainFactory.Cast(grain, typeof(bool)));
        }

        /// <summary>
        /// Tests internal casting mechanism for same-type casts.
        /// Validates that casting a grain to its own interface type is optimized
        /// as a no-op, returning the same reference object.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastInternalCastFromMyType()
        {
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), SimpleGrain.SimpleGrainNamePrefix);
            
            // This cast should be a no-op, since the interface matches the initial reference's exactly.
            IAddressable cast = grain.Cast<ISimpleGrain>();

            Assert.Same(grain, cast);
            Assert.IsAssignableFrom<ISimpleGrain>(cast);
        }

        /// <summary>
        /// Tests upcasting from derived to base grain interfaces.
        /// Validates that grain references can be cast from derived interfaces
        /// to their base interfaces, following normal inheritance rules.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastInternalCastUpFromChild()
        {
            GrainReference grain = (GrainReference)this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            
            // This cast should be a no-op, since the interface is implemented by the initial reference's interface.
            IAddressable cast = grain.Cast<IGeneratorTestGrain>();

            Assert.Same(grain, cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
        }

        /// <summary>
        /// Tests grain reference upcasting through AsReference method.
        /// Validates that derived grain references can be upcast to base interfaces
        /// using the AsReference API while maintaining type compatibility.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromChild()
        {
            GrainReference grain = (GrainReference) this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            GrainReference cast = (GrainReference) grain.AsReference<IGeneratorTestGrain>();
            Assert.IsAssignableFrom<IGeneratorTestDerivedGrain1>(cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
        }

        /// <summary>
        /// Tests that side-casting between sibling interfaces fails after grain resolution.
        /// Validates that attempting to cast between unrelated interfaces (siblings in
        /// the hierarchy) throws InvalidCastException when the grain is already resolved.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task FailSideCastAfterResolve()
        {
            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            Assert.True(await grain.StringIsNullOrEmpty());

            IGeneratorTestDerivedGrain2 cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
            
            await Assert.ThrowsAsync<InvalidCastException>(() => cast.StringConcat("a", "b", "c"));
        }

        /// <summary>
        /// Tests that operations fail after invalid side-cast attempts.
        /// Validates that calling methods on an incorrectly cast grain reference
        /// throws InvalidCastException when the actual grain doesn't implement the interface.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task FailOperationAfterSideCast()
        {
            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());

            // Cast works optimistically when the grain reference is not already resolved
            IGeneratorTestDerivedGrain2 cast = grain.AsReference<IGeneratorTestDerivedGrain2>();

            // Operation fails when grain reference is completely resolved
            await Assert.ThrowsAsync<InvalidCastException>(() => cast.StringConcat("a", "b", "c"));
        }

        /// <summary>
        /// Tests complex casting scenarios with continuation chains.
        /// Validates that invalid casts fail appropriately in async continuation contexts,
        /// while valid operations on common interfaces continue to work.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task FailSideCastAfterContinueWith()
        {
            var grain = GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            IGeneratorTestDerivedGrain2 cast = null;
            var av = grain.StringIsNullOrEmpty();
            var av2 = av.ContinueWith(t => Assert.True(t.Result))
                .ContinueWith(
                    t =>
                    {
                        Assert.False(t.IsFaulted);

                        // Casting is always allowed, so this should succeed.
                        cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
                    })
                .ContinueWith(
                    t =>
                    {
                        // Call a method which the grain does not implement, resulting in a cast failure.
                        Assert.True(t.IsCompletedSuccessfully);
                        return cast.StringConcat("a", "b", "c");
                    })
                .Unwrap()
                .ContinueWith(
                    t =>
                    {
                        // Call a method on the common interface, which the grain implements.
                        // This should not throw.
                        Assert.True(t.IsFaulted);
                        return cast.StringIsNullOrEmpty();
                    })
                .Unwrap();

            // Ensure that the last task did not throw.
            var av2Result = await av2;
            Assert.True(av2Result);
        }

        /// <summary>
        /// Tests upcasting through multiple levels of interface inheritance.
        /// Validates that grain references can be cast from grandchild interfaces
        /// to both parent and grandparent interfaces, with proper type checking
        /// to prevent invalid cross-hierarchy casts.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromGrandchild()
        {
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

        /// <summary>
        /// Tests upcasting from deeply nested derived interfaces.
        /// Validates proper type relationships when casting from interfaces
        /// that are multiple levels deep in the inheritance hierarchy.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromDerivedDerivedChild()
        {
            GrainReference grain = (GrainReference) this.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
            GrainReference cast = (GrainReference) grain.AsReference<IGeneratorTestDerivedGrain2>();
            Assert.IsAssignableFrom<IGeneratorTestDerivedDerivedGrain>(cast);
            Assert.IsAssignableFrom<IGeneratorTestDerivedGrain2>(cast);
            Assert.IsAssignableFrom<IGeneratorTestGrain>(cast);
            Assert.False(cast is IGeneratorTestDerivedGrain1);
        }

        /// <summary>
        /// Tests async operations on self-cast grain references.
        /// Validates that grain method calls work correctly after casting
        /// a grain reference to its own interface type.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task CastAsyncGrainRefCastFromSelf()
        {
            IAddressable grain = this.GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.Next(), SimpleGrain.SimpleGrainNamePrefix);
            ISimpleGrain cast = grain.AsReference<ISimpleGrain>();

            Task<int> successfulCallPromise = cast.GetA();
            await successfulCallPromise;
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
        /// <summary>
        /// Tests calling methods inherited from base interfaces after casting.
        /// Validates that methods defined in base interfaces remain accessible
        /// when working with derived grain references and after casting operations.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task CastCallMethodInheritedFromBaseClass()
        {
            Task<bool> isNullStr;

            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            isNullStr = grain.StringIsNullOrEmpty();
            Assert.True(await isNullStr, "Value should be null initially");

            isNullStr = grain.StringSet("a").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.False(await isNullStr, "Value should not be null after SetString(a)");

            isNullStr = grain.StringSet(null).ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.True(await isNullStr, "Value should be null after SetString(null)");

            IGeneratorTestGrain cast = grain.AsReference<IGeneratorTestGrain>();
            isNullStr = cast.StringSet("b").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.False(await isNullStr, "Value should not be null after cast.SetString(b)");
        }
    }
}
