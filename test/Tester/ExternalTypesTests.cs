using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System.Collections.Generic;
using System.Collections.Specialized;
using Xunit;
using Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    public class ExternalTypesTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithAbstractExternalTypeParam()
        {
            var grainWitAbstractTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWitAbstractTypeParam.GetAbstractModel(new List<NameObjectCollectionBase>() { new NameValueCollection() });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithEnumExternalTypeParam()
        {
            var grainWithEnumTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWithEnumTypeParam.GetEnumModel();
        }
    }
}
