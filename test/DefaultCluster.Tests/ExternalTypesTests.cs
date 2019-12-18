using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class ExternalTypesTests : HostedTestClusterEnsureDefaultStarted
    {
        public ExternalTypesTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

// Note: .NET Core does not implement serialization for NameValueCollection, so this test is not valid on .NET Core.
#if !NETCOREAPP
        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithAbstractExternalTypeParam()
        {
            var grainWitAbstractTypeParam = this.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWitAbstractTypeParam.GetAbstractModel(new List<NameObjectCollectionBase>() { new NameValueCollection() });
        }
#endif

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithEnumExternalTypeParam()
        {
            var grainWithEnumTypeParam = this.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWithEnumTypeParam.GetEnumModel();
        }
    }
}
