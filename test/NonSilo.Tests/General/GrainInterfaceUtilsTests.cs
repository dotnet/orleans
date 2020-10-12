using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace NonSilo.Tests.General
{
    [TestCategory("BVT")]
    public class GrainInterfaceUtilsTests
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

        [Fact]
        public void Generic_InterfaceId_Test()
        {
            var expected = GrainInterfaceUtils.GetGrainInterfaceId(typeof(ISimpleGenericGrain<>));
            IDictionary<int, Type> actual = GrainInterfaceUtils.GetRemoteInterfaces(typeof(SimpleGenericGrain<>));
            Assert.Contains(expected, actual);
        }
    }
}

