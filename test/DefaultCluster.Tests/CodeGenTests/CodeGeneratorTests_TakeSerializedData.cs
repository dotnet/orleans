using System;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    public class CodeGeneratorTests_TakeSerializedData : HostedTestClusterEnsureDefaultStarted
    {
        public CodeGeneratorTests_TakeSerializedData(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task TakeSerializedDataNotRefOrleans()
        {
            var grain = this.GrainFactory.GetGrain<ISerializerPresenceTest>(Guid.NewGuid());
            await grain.TakeSerializedData(new UnitTests.Dtos.ClassNotReferencingOrleansTypeDto { MyProperty = "Test" });
        }

        [Fact(Skip = "reproduces issue #1480"), TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task TakeSerializedDataRefOrleans()
        {
            var grain = this.GrainFactory.GetGrain<ISerializerPresenceTest>(Guid.NewGuid());
            await grain.TakeSerializedData(new UnitTests.DtosRefOrleans.ClassReferencingOrleansTypeDto { MyProperty = "Test" });
        }
    }
}
