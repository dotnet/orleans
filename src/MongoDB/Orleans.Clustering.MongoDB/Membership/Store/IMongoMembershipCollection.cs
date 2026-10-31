using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal interface IMongoMembershipCollection
{
    Task CleanupDefunctSiloEntries(string deploymentId, DateTimeOffset beforeDate, CancellationToken cancellationToken = default);

    Task DeleteMembershipTableEntries(string deploymentId, CancellationToken cancellationToken = default);

    Task<IList<Uri>> GetGateways(string deploymentId, CancellationToken cancellationToken = default);

    Task<MembershipTableData> ReadAll(string deploymentId, CancellationToken cancellationToken = default);

    Task<MembershipTableData> ReadRow(string deploymentId, SiloAddress address, CancellationToken cancellationToken = default);

    Task UpdateIAmAlive(string deploymentId, SiloAddress address, DateTime iAmAliveTime, CancellationToken cancellationToken = default);

    Task<bool> UpsertRow(string deploymentId, MembershipEntry entry, string? etag, TableVersion tableVersion, CancellationToken cancellationToken = default);
}
