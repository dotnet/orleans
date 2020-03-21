using Orleans.CodeGeneration;
using UnitTests.GrainInterfaces;
using Xunit;

namespace NonSilo.Tests.General
{
    [TestCategory("BVT")]
    public class TypeCodeOverrideTests
    {
        [Fact]
        public void Override_MethodId_Test()
        {
            var methodId = GrainInterfaceUtils.ComputeMethodId(
                typeof(IMethodInterceptionGrain).GetMethod(nameof(IMethodInterceptionGrain.One)));
            Assert.Equal(14142, methodId);

            methodId = GrainInterfaceUtils.ComputeMethodId(
                typeof(IMethodInterceptionGrain).GetMethod(nameof(IMethodInterceptionGrain.Echo)));
            Assert.Equal(-14142, methodId);
        }

        [Fact]
        public void Override_InterfaceId_Test()
        {
            var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(typeof(IMethodInterceptionGrain));
            Assert.Equal(6548972, interfaceId);

            interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(typeof(IOutgoingMethodInterceptionGrain));
            Assert.Equal(-6548972, interfaceId);
        }
    }
}
