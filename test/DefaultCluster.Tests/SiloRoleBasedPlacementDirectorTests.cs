using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests.General
{
    public class SiloRoleBasedPlacementDirectorTests : HostedTestClusterEnsureDefaultStarted
    {
        public SiloRoleBasedPlacementDirectorTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("Functional")]
        public async Task SiloRoleBasedPlacementDirector_CantFindSilo()
        {
            var grain = this.GrainFactory.GetGrain<ISiloRoleBasedPlacementGrain>("Sibyl.Silo");
            await Assert.ThrowsAsync<OrleansException>(() => grain.Ping());
        }

        [Fact, TestCategory("Functional")]
        public async Task SiloRoleBasedPlacementDirector_CanFindSilo()
        {
            var grain = this.GrainFactory.GetGrain<ISiloRoleBasedPlacementGrain>("testhost");
            bool result = await grain.Ping();
            Assert.True(result);
        }
    }
}
