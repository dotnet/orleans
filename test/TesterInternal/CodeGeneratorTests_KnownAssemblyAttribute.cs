using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System;
using Microsoft.FSharp.Core;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

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
            Assert.IsTrue(serializerExists);
        }

        private void ClientSerializerExists(Type t)
        {
            Assert.IsTrue(Orleans.Serialization.SerializationManager.HasSerializer(t));
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

        //TODO: FIX ME - F# 
        //[Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        //public async Task Silo_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        //{
        //    await SiloSerializerExists(typeof(FSharpTypes.SingleCaseDU));
        //}

        //TODO: FIX ME - F# 
        //[Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("Serialization")]
        //public void Client_Serializer_Exists_for_Type_In_Grain_Assembly_containing_KnownAssemblyAttribute()
        //{
        //    ClientSerializerExists(typeof(FSharpTypes.SingleCaseDU));
        //}
    }
}
