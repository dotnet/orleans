using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Xunit;
using NSubstitute;
using Orleans.Runtime;
using System;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for <see cref="MembershipTableManager"/>
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableManagerTests
    {
        /*
        [Fact]
        public async Task MembershipTableManager_InitialSnapshot()
        {
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(SiloAddress.FromParsableString("127.0.0.1:100@1"));
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: null,
                fatalErrorHandler: null,
                gossiper: null,
                log: null,
                loggerFactory: null);
            await manager.Sta
        }*/

        // Initial snapshots are valid
        // Table refresh:
        // * Does period refresh
        // * Quick retry after exception
        // * Emits change notification
        // TrySuspectOrKill tests:
        // Lifecycle tests:
        // * Correct status updates at each level
        // * Verify own memberhip table entry has correct properties
        // * Cleans up old entries for same silo
        // * Graceful & ungraceful shutdown
        // * Timer stalls?
        // * Snapshot updated + change notification emitted after status update
        // Node migration
        // Fault on missing entry during refresh
        // Fault on declared dead
        // Gossips on updates
    }

    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipAgentTests
    {
        // Lifecycle tests:
        // * Correct status updates at each level
        // ValidateInitialConnectivity
        // * Enabled vs Disabled
        // UpdateIAmAlive tests
        // * Periodically updated
        // * Correct values
        // * Missing entry
        // * Quick retry after exception
        // Graceful vs ungraceful shutdown
    }

    [TestCategory("BVT"), TestCategory("Membership")]
    public class ClusterHealthMonitorTests
    {
        // Periodically checks health
        // Selects correct subset of silos
        // TrySuspectOrKill on error
        // Graceful vs ungraceful shutdown
        // Processes membership updates
    }

    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableCleanupAgentTests
    {
        // Configuration tests:
        // * Clean startup & shutdown when enabled vs disabled
        // Cleans up old, dead entries only
        // Skip cleanup if any entry has timestamp newer than current time?
    }
}
