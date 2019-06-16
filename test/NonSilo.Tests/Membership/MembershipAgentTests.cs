namespace NonSilo.Tests.Membership
{
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
}
