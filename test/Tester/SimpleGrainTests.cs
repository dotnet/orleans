using System.Threading.Tasks;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    public class SimpleGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        public ISimpleGrain GetSimpleGrain()
        {
            return GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId(), SimpleGrain.SimpleGrainNamePrefix);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SimpleGrainGetGrain()
        {
            ISimpleGrain grain = GetSimpleGrain();
            int ignored = await grain.GetAxB();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
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

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SimpleGrainDataFlow()
        {
            ISimpleGrain grain = GetSimpleGrain();

            Task setAPromise = grain.SetA(3);
            Task setBPromise = grain.SetB(4);
            await Task.WhenAll(setAPromise, setBPromise);
            var x = await grain.GetAxB();

            Assert.Equal(12, x);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task GettingGrainWithMultipleConstructorsActivesViaDefaultConstructor()
        {
            ISimpleGrain grain = GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId(), grainClassNamePrefix: MultipleConstructorsSimpleGrain.MultipleConstructorsSimpleGrainPrefix);

            var actual = await grain.GetA();
            Assert.Equal(MultipleConstructorsSimpleGrain.ValueUsedByParameterlessConstructor, actual);
        }
    }
}
