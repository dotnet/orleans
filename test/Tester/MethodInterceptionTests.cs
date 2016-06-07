﻿namespace Tester
{
    using System.Threading.Tasks;
    using Orleans;
    using UnitTests.GrainInterfaces;
    using UnitTests.Tester;
    using Xunit;
    
    public class MethodInterceptionTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("MethodInterception")]
        public async Task GrainMethodInterceptionTest()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMethodInterceptionGrain>(0);
            var result = await grain.One();
            Assert.Equal("intercepted one with no args", result, "Method invocation should have been intercepted");

            result = await grain.Echo("stao erom tae");
            Assert.Equal(
                "eat more oats",
                result,
                "Grain interceptors should receive the MethodInfo of the implementation, not the interface.");

            result = await grain.NotIntercepted();
            Assert.Equal("not intercepted", result);
        }
    }
}
