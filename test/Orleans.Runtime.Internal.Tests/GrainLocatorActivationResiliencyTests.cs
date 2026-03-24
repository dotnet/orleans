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
    /// 
    /// The grain directory can contain stale or "poison" entries that point to
    /// non-existent activations. This can happen due to:
    /// - Silo crashes before cleaning up directory entries
    /// - Network partitions causing inconsistent state
    /// - Race conditions during activation/deactivation
    /// 
    /// Orleans must be resilient to these scenarios by:
    /// - Detecting invalid entries during activation
    /// - Retrying registration with proper previousRegistration parameter
    /// - Forwarding calls to the correct silo based on directory entries
    /// 
    /// These tests verify Orleans can recover from poison directory entries
    /// and maintain the single activation constraint.
    /// </summary>
    public class GrainLocatorActivationResiliencyTests : HostedTestClusterEnsureDefaultStarted
    {
        public GrainLocatorActivationResiliencyTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests recovery when the grain directory contains an invalid entry pointing
        /// to the same silo where activation will occur.
        /// 
        /// Scenario:
        /// 1. Insert a "poison" directory entry pointing to a non-existent activation on the primary silo
        /// 2. Attempt to activate the grain on the same silo
        /// 3. The activation should detect the invalid entry (same silo, different activation ID)
        /// 4. Retry registration with the poison entry as previousRegistration to update it
        /// 
        /// This tests the local silo's ability to self-heal invalid directory state.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ReactivateGrainWithPoisonGrainDirectoryEntry_LocalSilo()
        {
            var primarySilo = (InProcessSiloHandle)Fixture.HostedCluster.Primary;
            var primarySiloAddress = primarySilo.SiloAddress;
            var grain = GrainFactory.GetGrain<IGuidTestGrain>(Guid.NewGuid());

            // Create a poison directory entry:
            // - Points to the correct silo (primary)
            // - But with a fake activation ID that doesn't exist
            // This simulates a stale entry from a crashed/deactivated grain
            var grainLocator = primarySilo.SiloHost.Services.GetRequiredService<GrainLocator>();
            var badAddress = GrainAddress.GetAddress(primarySiloAddress, grain.GetGrainId(), ActivationId.NewId());
            await grainLocator.Register(badAddress, previousRegistration: null);

            {
                // Force placement on the primary silo using placement hint
                // Despite the poison entry, the grain should successfully activate
                RequestContext.Set(IPlacementDirector.PlacementHintKey, primarySiloAddress);
                var silo = await grain.GetSiloAddress();
                Assert.Equal(primarySiloAddress, silo);
            }
        }
        
        /// <summary>
        /// Tests recovery when activation is attempted on a different silo than the poison entry.
        /// 
        /// Scenario:
        /// 1. Insert poison entry pointing to primary silo
        /// 2. Attempt to activate on secondary silo
        /// 3. Secondary silo sees the directory entry and forwards the call to primary
        /// 4. Primary silo detects invalid entry and updates it during activation
        /// 
        /// This tests cross-silo coordination and forwarding behavior when dealing
        /// with invalid directory entries, ensuring calls reach the right silo.
        /// </summary>
        [Fact, TestCategory("BVT")]
        public async Task ReactivateGrainWithPoisonGrainDirectoryEntry_RemoteSilo()
        {
            var primarySilo = (InProcessSiloHandle)Fixture.HostedCluster.Primary;
            var secondarySiloAddress = Fixture.HostedCluster.SecondarySilos.First().SiloAddress;
            var primarySiloAddress = primarySilo.SiloAddress;
            var grain = GrainFactory.GetGrain<IGuidTestGrain>(Guid.NewGuid());

            // Create poison entry pointing to primary silo with fake activation ID
            // This tests the forwarding mechanism: secondary silo will see this entry
            // and forward the request to primary, where recovery will occur
            var grainLocator = primarySilo.SiloHost.Services.GetRequiredService<GrainLocator>();
            var badAddress = GrainAddress.GetAddress(primarySiloAddress, grain.GetGrainId(), ActivationId.NewId());
            await grainLocator.Register(badAddress, previousRegistration: null);

            {
                // Attempt placement on secondary silo, but the directory entry
                // will cause forwarding to primary silo. This verifies:
                // 1. Secondary recognizes it shouldn't activate due to existing entry
                // 2. Call is forwarded to primary based on directory
                // 3. Primary recovers from poison entry and activates successfully
                RequestContext.Set(IPlacementDirector.PlacementHintKey, secondarySiloAddress);
                var silo = await grain.GetSiloAddress();
                Assert.Equal(primarySiloAddress, silo);
            }
        }
    }
}
