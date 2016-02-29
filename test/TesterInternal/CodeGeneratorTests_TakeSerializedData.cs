using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class CodeGeneratorTests_TakeSerializedData : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task TakeSerializedDataNotRefOrleans()
        {
            var grain = GrainFactory.GetGrain<ISerializerPresenceTest>(Guid.NewGuid());
            await grain.TakeSerializedData(new Dtos.ClassNotReferencingOrleansTypeDto { MyProperty = "Test" });
        }

        [Fact(Skip = "reproduces issue #1480"), TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task TakeSerializedDataRefOrleans()
        {
            var grain = GrainFactory.GetGrain<ISerializerPresenceTest>(Guid.NewGuid());
            await grain.TakeSerializedData(new DtosRefOrleans.ClassReferencingOrleansTypeDto { MyProperty = "Test" });
        }
    }
}
