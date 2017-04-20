using System;
using System.Globalization;
using TestExtensions;
using UnitTests.Grains;

namespace DefaultCluster.Tests
{
    using System.Threading.Tasks;
    using Orleans;
    using UnitTests.GrainInterfaces;
    using Xunit;

    [TestCategory("MethodInterception")]
    public class MethodInterceptionTests : HostedTestClusterEnsureDefaultStarted
    {
        public MethodInterceptionTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT")]
        public async Task GrainMethodInterceptionTest()
        {
            var grain = this.GrainFactory.GetGrain<IMethodInterceptionGrain>(0);
            var result = await grain.One();
            Assert.Equal("intercepted one with no args", result);

            result = await grain.Echo("stao erom tae");
            Assert.Equal("eat more oats", result);// Grain interceptors should receive the MethodInfo of the implementation, not the interface.

            result = await grain.NotIntercepted();
            Assert.Equal("not intercepted", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);
        }

        [Fact, TestCategory("BVT")]
        public async Task GenericGrainMethodInterceptionTest()
        {
            var grain = this.GrainFactory.GetGrain<IGenericMethodInterceptionGrain<int>>(0);
            var result = await grain.GetInputAsString(679);
            Assert.Contains("Hah!", result);
            Assert.Contains("679", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);
        }

        [Fact, TestCategory("BVT")]
        public async Task ConstructedInheritanceGenericGrainMethodInterceptionTest()
        {
            var grain = this.GrainFactory.GetGrain<ITrickyMethodInterceptionGrain>(0);

            var result = await grain.GetInputAsString("2014-12-19T14:32:50Z");
            Assert.Contains("Hah!", result);
            Assert.Contains("2014-12-19T14:32:50Z", result);

            result = await grain.SayHello();
            Assert.Equal("Hello", result);

            var bestNumber = await grain.GetBestNumber();
            Assert.Equal(38, bestNumber);

            result = await grain.GetInputAsString(true);
            Assert.Contains(true.ToString(CultureInfo.InvariantCulture), result);
        }
    }
}
