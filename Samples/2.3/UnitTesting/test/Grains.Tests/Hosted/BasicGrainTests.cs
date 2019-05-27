using System;
using System.Threading.Tasks;
using Grains.Tests.Hosted.Cluster;
using Orleans.TestingHost;
using Xunit;

namespace Grains.Tests.Hosted
{
    [Collection(nameof(ClusterCollection))]
    public class BasicGrainTests
    {
        private readonly TestCluster cluster;

        public BasicGrainTests(ClusterFixture fixture)
        {
            cluster = fixture.Cluster;
        }

        [Fact]
        public async Task Gets_And_Sets_Value()
        {
            // get a new basic grain from the cluster
            var grain = cluster.GrainFactory.GetGrain<IBasicGrain>(Guid.NewGuid());

            // assert the default value is zero
            Assert.Equal(0, await grain.GetValueAsync());

            // set a new value
            await grain.SetValueAsync(123);

            // assert the new value is as set
            Assert.Equal(123, await grain.GetValueAsync());
        }
    }
}