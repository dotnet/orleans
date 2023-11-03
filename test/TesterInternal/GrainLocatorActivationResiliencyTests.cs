using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests
{
    /// <summary>
    /// Tests that grain activation can recover from invalid grain directory entries.
    /// </summary>
    public class GrainLocatorActivationResiliencyTests : HostedTestClusterEnsureDefaultStarted
    {
        public GrainLocatorActivationResiliencyTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that a grain can be activated even if the grain locator indicates that there is an existing registration for that grain on the same silo.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ReactivateGrainWithPoisonGrainDirectoryEntry_LocalSilo()
        {
            var primarySilo = (InProcessSiloHandle)Fixture.HostedCluster.Primary;
            var primarySiloAddress = primarySilo.SiloAddress;
            var grain = GrainFactory.GetGrain<IGuidTestGrain>(Guid.NewGuid());

            // Insert an entry into the grain directory which points to a grain which is not active.
            // The entry points to the silo which the activation will be created on, but it points to a different activation.
            // This will cause the first registration attempt to fail, but the activation should recognize that the grain
            // directory entry is incorrect (points to the current silo, but an activation which must be defunct) and retry
            // registration, passing the previous registration so that the grain directory entry is updated.
            var grainLocator = primarySilo.SiloHost.Services.GetRequiredService<GrainLocator>();
            var badAddress = GrainAddress.GetAddress(primarySiloAddress, grain.GetGrainId(), ActivationId.NewId());
            await grainLocator.Register(badAddress, previousRegistration: null);

            {
                // Rig placement to occurs on the primary silo.
                RequestContext.Set(IPlacementDirector.PlacementHintKey, primarySiloAddress);
                var silo = await grain.GetSiloAddress();
                Assert.Equal(primarySiloAddress, silo);
            }
        }
        
        /// <summary>
        /// Similar to <see cref="ReactivateGrainWithPoisonGrainDirectoryEntry_LocalSilo"/>, except this test attempts to activate the grain on a different silo
        /// from the one specified in the grain directory entry, to ensure that the call will be forwarded to the correct silo.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ReactivateGrainWithPoisonGrainDirectoryEntry_RemoteSilo()
        {
            var primarySilo = (InProcessSiloHandle)Fixture.HostedCluster.Primary;
            var secondarySiloAddress = Fixture.HostedCluster.SecondarySilos.First().SiloAddress;
            var primarySiloAddress = primarySilo.SiloAddress;
            var grain = GrainFactory.GetGrain<IGuidTestGrain>(Guid.NewGuid());

            // Insert an entry into the grain directory which points to a grain which is not active.
            // The entry points to another silo, but that silo also does not host the registered activation.
            // This will cause the first registration attempt to fail, but the activation will forward messages to the
            // silo which the registration points to and the subsequent activation will also initially fail registration,
            // but succeed later.
            var grainLocator = primarySilo.SiloHost.Services.GetRequiredService<GrainLocator>();
            var badAddress = GrainAddress.GetAddress(primarySiloAddress, grain.GetGrainId(), ActivationId.NewId());
            await grainLocator.Register(badAddress, previousRegistration: null);

            {
                // Rig placement to occurs on the secondary silo, but since there is a directory entry pointing to the primary silo,
                // the call should be forwarded to the primary silo where the grain will be activated.
                RequestContext.Set(IPlacementDirector.PlacementHintKey, secondarySiloAddress);
                var silo = await grain.GetSiloAddress();
                Assert.Equal(primarySiloAddress, silo);
            }
        }
    }
}
