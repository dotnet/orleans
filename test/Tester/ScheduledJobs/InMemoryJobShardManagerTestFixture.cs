using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.DurableJobs;

namespace Tester.DurableJobs;

/// <summary>
/// InMemory implementation of <see cref="IJobShardManagerTestFixture"/>.
/// Provides the infrastructure needed to run shared job shard manager tests against the InMemory provider.
/// </summary>
internal sealed class InMemoryJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    public InMemoryJobShardManagerTestFixture()
    {
        // Clear any state from previous tests
        InMemoryJobShardManager.ClearAllShardsAsync().GetAwaiter().GetResult();
    }

    public JobShardManager CreateManager(ILocalSiloDetails localSiloDetails, IClusterMembershipService membershipService)
    {
        return new InMemoryJobShardManager(localSiloDetails.SiloAddress, membershipService);
    }

    public async ValueTask DisposeAsync()
    {
        // Clear state after tests
        await InMemoryJobShardManager.ClearAllShardsAsync();
    }
}
