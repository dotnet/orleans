using System;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    public class KnownAssemblyAttributeTests : HostedTestClusterEnsureDefaultStarted
    {
        public KnownAssemblyAttributeTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private async Task SiloSerializerExists(Type t)
        {
            var id = Guid.NewGuid();
            var grain = this.GrainFactory.GetGrain<ISerializerPresenceTest>(id);
            var serializerExists = await grain.SerializerExistsForType(t);
            Assert.True(serializerExists);
        }

        private void ClientSerializerExists(Type t)
        {
            Assert.True(this.HostedCluster.GetSerializer().CanSerialize(t));
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly()
        {
            await SiloSerializerExists(typeof(UnitTests.Grains.SimpleGrainState));
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Grain_Assembly()
        {
            ClientSerializerExists(typeof(UnitTests.Grains.SimpleGrainState));
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Known_Assembly()
        {
            await SiloSerializerExists(typeof(FSharpOption<int>));
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Known_Assembly()
        {
            ClientSerializerExists(typeof(FSharpOption<int>));
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        {
            await SiloSerializerExists(typeof(UnitTests.FSharpTypes.SingleCaseDU));
        }

        [Fact, TestCategory("BVT"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        {
            ClientSerializerExists(typeof(UnitTests.FSharpTypes.SingleCaseDU));
        }
    }
}
