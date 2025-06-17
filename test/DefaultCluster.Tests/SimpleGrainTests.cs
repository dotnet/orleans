using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Basic tests for simple grain functionality in Orleans.
    /// These tests verify fundamental grain operations including activation,
    /// method invocation, state management, and basic control flow.
    /// SimpleGrain represents the most basic grain pattern with getter/setter
    /// methods and demonstrates core Orleans programming model concepts.
    /// </summary>
    public class SimpleGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        public SimpleGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        public ISimpleGrain GetSimpleGrain()
        {
            return this.GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId(), SimpleGrain.SimpleGrainNamePrefix);
        }

        /// <summary>
        /// Tests basic grain activation and method invocation.
        /// Verifies that a grain can be obtained from the factory and
        /// that methods can be successfully called on it, demonstrating
        /// the fundamental grain lifecycle and RPC mechanism.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task SimpleGrainGetGrain()
        {
            ISimpleGrain grain = GetSimpleGrain();
            _ = await grain.GetAxB();
        }

        /// <summary>
        /// Tests sequential grain method calls and state persistence.
        /// Verifies that grain state is maintained between calls by
        /// setting values through separate method calls and then
        /// computing a result based on the persisted state.
        /// Demonstrates grain activation persistence within a session.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task SimpleGrainControlFlow()
        {
            ISimpleGrain grain = GetSimpleGrain();
            
            Task setPromise = grain.SetA(2);
            await setPromise;

            setPromise = grain.SetB(3);
            await setPromise;

            Task<int> intPromise = grain.GetAxB();
            Assert.Equal(6, await intPromise);
        }

        /// <summary>
        /// Tests concurrent grain method calls and data consistency.
        /// Verifies that multiple method calls can be issued concurrently
        /// (using Task.WhenAll) and that the grain properly handles
        /// concurrent operations while maintaining state consistency.
        /// Demonstrates Orleans' turn-based concurrency model.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task SimpleGrainDataFlow()
        {
            ISimpleGrain grain = GetSimpleGrain();

            Task setAPromise = grain.SetA(3);
            Task setBPromise = grain.SetB(4);
            await Task.WhenAll(setAPromise, setBPromise);
            var x = await grain.GetAxB();

            Assert.Equal(12, x);
        }

        /// <summary>
        /// Tests grain activation with multiple constructors.
        /// Would verify that grains with multiple constructors activate
        /// using the default (parameterless) constructor.
        /// NOTE: Currently skipped as grains with multiple constructors
        /// require explicit registration in the current Orleans version.
        /// </summary>
        [Fact(Skip = "Grains with multiple constructors are not supported without being explicitly registered.")]
        [TestCategory("BVT")]
        public async Task GettingGrainWithMultipleConstructorsActivesViaDefaultConstructor()
        {
            ISimpleGrain grain = this.GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId(), grainClassNamePrefix: MultipleConstructorsSimpleGrain.MultipleConstructorsSimpleGrainPrefix);

            var actual = await grain.GetA();
            Assert.Equal(MultipleConstructorsSimpleGrain.ValueUsedByParameterlessConstructor, actual);
        }
    }
}
