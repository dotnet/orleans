using System.Threading.Tasks;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class ClusterHealthMonitorTests
    {
        // Periodically checks health
        // Selects correct subset of silos
        // TrySuspectOrKill on error
        // Graceful vs ungraceful shutdown
        // Processes membership updates
    }
}
