namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT")]
    public class MembershipTableManagerTests
    {
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

    [TestCategory("BVT")]
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

    [TestCategory("BVT")]
    public class ClusterHealthMonitorTests
    {
        // Periodically checks health
        // Selects correct subset of silos
        // TrySuspectOrKill on error
        // Graceful vs ungraceful shutdown
        // Processes membership updates
    }

    [TestCategory("BVT")]
    public class MembershipTableCleanupAgentTests
    {
        // Configuration tests:
        // * Clean startup & shutdown when enabled vs disabled
        // Cleans up old, dead entries only
        // Skip cleanup if any entry has timestamp newer than current time?
    }
}
