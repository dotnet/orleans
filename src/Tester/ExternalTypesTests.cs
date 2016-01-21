using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class ExternalTypesTests : HostedTestClusterEnsureDefaultStarted
    {
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithAbstractExternalTypeParam()
        {
            var grainWitAbstractTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWitAbstractTypeParam.GetAbstractModel(new List<NameObjectCollectionBase>() { new NameValueCollection() });
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithEnumExternalTypeParam()
        {
            var grainWithEnumTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWithEnumTypeParam.GetEnumModel();
        }
    }
}
