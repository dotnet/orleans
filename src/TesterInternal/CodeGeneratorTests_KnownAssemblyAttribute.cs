using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class KnownAssemblyAttributeTests : HostedTestClusterEnsureDefaultStarted
    {

        private async Task SiloSerializerExists(Type t)
        {
            var id = Guid.NewGuid();
            var grain = GrainFactory.GetGrain<ISerializerPresenceTest>(id);
            var serializerExists = await grain.SerializerExistsForType(t);
            Assert.IsTrue(serializerExists);
        }

        private void ClientSerializerExists(Type t)
        {
            Assert.IsTrue(Orleans.Serialization.SerializationManager.HasSerializer(t));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly()
        {
            await SiloSerializerExists(typeof(Grains.SimpleGrainState));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Grain_Assembly()
        {
            ClientSerializerExists(typeof(Grains.SimpleGrainState));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Known_Assembly()
        {
            await SiloSerializerExists(typeof(FSharpOption<>));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Known_Assembly()
        {
            ClientSerializerExists(typeof(FSharpOption<>));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        {
            await SiloSerializerExists(typeof(FSharpTypes.SingleCaseDU));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        public void Client_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        {
            ClientSerializerExists(typeof(FSharpTypes.SingleCaseDU));
        }
    }
}
