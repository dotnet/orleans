namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableCleanupAgentTests
    {
        // Configuration tests:
        // * Clean startup & shutdown when enabled vs disabled
        // Cleans up old, dead entries only
        // Skip cleanup if any entry has timestamp newer than current time?
    }
}
