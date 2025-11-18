using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.DurableJobs;

namespace Tester.DurableJobs;

/// <summary>
/// Defines the contract for provider-specific test fixtures used by <see cref="JobShardManagerTestsRunner"/>.
/// Each provider implementation (Azure, InMemory, etc.) should implement this interface to provide
/// the necessary infrastructure for running shared job shard manager tests.
/// </summary>
public interface IJobShardManagerTestFixture : IAsyncDisposable
{
    /// <summary>
    /// Creates a new <see cref="JobShardManager"/> instance for the specified silo.
    /// </summary>
    /// <param name="localSiloDetails">The local silo details.</param>
    /// <param name="membershipService">The cluster membership service for the manager.</param>
    /// <returns>A configured job shard manager instance.</returns>
    JobShardManager CreateManager(ILocalSiloDetails localSiloDetails, IClusterMembershipService membershipService);
}
