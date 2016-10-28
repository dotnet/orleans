using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    public class ExternalTypesTests : HostedTestClusterEnsureDefaultStarted
    {
#if !NETSTANDARD_TODO
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithAbstractExternalTypeParam()
        {
            var grainWitAbstractTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWitAbstractTypeParam.GetAbstractModel(new List<NameObjectCollectionBase>() { new NameValueCollection() });
        }
#endif

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithEnumExternalTypeParam()
        {
            var grainWithEnumTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWithEnumTypeParam.GetEnumModel();
        }
    }
}
