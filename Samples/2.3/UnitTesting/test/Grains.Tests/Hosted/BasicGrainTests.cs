using System;
using System.Threading.Tasks;
using Grains.Tests.Hosted.Cluster;
using Xunit;

namespace Grains.Tests.Hosted
{
    [Collection(nameof(ClusterCollection))]
    public class BasicGrainTests
    {
        private readonly ClusterFixture fixture;

        public BasicGrainTests(ClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Gets_And_Sets_Value()
        {
            // get a new basic grain from the cluster
            var grain = fixture.Cluster.GrainFactory.GetGrain<IBasicGrain>(Guid.NewGuid());

            // assert the default value is zero
            Assert.Equal(0, await grain.GetValueAsync());

            // set a new value
            await grain.SetValueAsync(123);

            // assert the new value is as set
            Assert.Equal(123, await grain.GetValueAsync());
        }
    }
}