namespace Tester
{
    using System.Threading.Tasks;
    using Orleans;
    using UnitTests.GrainInterfaces;
    using UnitTests.Tester;
    using Xunit;
    using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

    public class MethodInterceptionTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("MethodInterception")]
        public async Task GrainMethodInterceptionTest()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMethodInterceptionGrain>(0);
            var result = await grain.One();
            Assert.AreEqual("intercepted one with no args", result, "Method invocation should have been intercepted");

            result = await grain.Echo("stao erom tae");
            Assert.AreEqual(
                "eat more oats",
                result,
                "Grain interceptors should receive the MethodInfo of the implementation, not the interface.");

            result = await grain.NotIntercepted();
            Assert.AreEqual("not intercepted", result);
        }
    }
}
