using System;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    public class KnownAssemblyAttributeTests : HostedTestClusterEnsureDefaultStarted
    {
        private async Task SiloSerializerExists(Type t)
        {
            var id = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<ISerializerPresenceTest>(id);
            var serializerExists = await grain.SerializerExistsForType(t);
            Assert.True(serializerExists);
        }

        private void ClientSerializerExists(Type t)
        {
            Assert.True(Orleans.Serialization.SerializationManager.HasSerializer(t));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly()
        {
            await SiloSerializerExists(typeof(Grains.SimpleGrainState));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Grain_Assembly()
        {
            ClientSerializerExists(typeof(Grains.SimpleGrainState));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Known_Assembly()
        {
            await SiloSerializerExists(typeof(FSharpOption<>));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Known_Assembly()
        {
            ClientSerializerExists(typeof(FSharpOption<>));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        {
            await SiloSerializerExists(typeof(FSharpTypes.SingleCaseDU));
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        {
            ClientSerializerExists(typeof(FSharpTypes.SingleCaseDU));
        }
    }
}
