using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Orleans.Clustering.MongoDB;
using Orleans.Runtime;

namespace Orleans.Providers.MongoDB.Membership.Store.Single;

internal sealed class SingleMembershipCollection : CollectionBase<DeploymentDocument>, IMongoMembershipCollection
{
    private static readonly TableVersion NotFound = new TableVersion(0, "0");
    private readonly string? collectionPrefix;

    public SingleMembershipCollection(IMongoClient mongoClient, string databaseName, string? collectionPrefix, bool createShardKey,
        Action<MongoCollectionSettings>? collectionConfigurator)
        : base(mongoClient, databaseName, collectionConfigurator, createShardKey)
    {
        this.collectionPrefix = collectionPrefix;
    }

    protected override string CollectionName()
    {
        return $"{collectionPrefix}OrleansMembershipSingle";
    }

    public async Task CleanupDefunctSiloEntries(string deploymentId, DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        var deployment = await GetCollection(cancellationToken).Find(x => x.DeploymentId == deploymentId).FirstOrDefaultAsync(cancellationToken);

        if (deployment is null)
        {
            return;
        }

        var beforeUtc = beforeDate.UtcDateTime;
        foreach (var kvp in deployment.Members)
        {
            var member = kvp.Value;

            if (member.IsDefunctBefore(beforeUtc))
            {
                // Recheck eligibility atomically with removal so a concurrent heartbeat is preserved.
                var memberKey = $"Members.{kvp.Key}";
                await GetCollection(cancellationToken).UpdateOneAsync(
                    Filter.And(
                        Filter.Eq(x => x.DeploymentId, deploymentId),
                        MembershipBase.DefunctBefore<DeploymentDocument>(beforeUtc, $"{memberKey}.")),
                    Update.Unset(memberKey), cancellationToken: cancellationToken);
            }
        }
    }

    public Task DeleteMembershipTableEntries(string deploymentId, CancellationToken cancellationToken = default)
    {
        return GetCollection(cancellationToken).DeleteOneAsync(x => x.DeploymentId == deploymentId, cancellationToken: cancellationToken);
    }

    public async Task<IList<Uri>> GetGateways(string deploymentId, CancellationToken cancellationToken = default)
    {
        var deployment = await GetCollection(cancellationToken).Find(x => x.DeploymentId == deploymentId).FirstOrDefaultAsync(cancellationToken);

        if (deployment == null)
        {
            return new List<Uri>();
        }

        return deployment.Members.Values.Where(x => x.Status == (int)SiloStatus.Active && x.ProxyPort > 0).Select(x => x.ToGatewayUri()).ToList();
    }

    public async Task<MembershipTableData> ReadAll(string deploymentId, CancellationToken cancellationToken = default)
    {
        var deployment = await GetCollection(cancellationToken).Find(x => x.DeploymentId == deploymentId).FirstOrDefaultAsync(cancellationToken);

        if (deployment == null)
        {
            return new MembershipTableData(NotFound);
        }

        return deployment.ToTable();
    }

    public async Task<MembershipTableData> ReadRow(string deploymentId, SiloAddress address, CancellationToken cancellationToken = default)
    {
        var deployment = await GetCollection(cancellationToken).Find(x => x.DeploymentId == deploymentId).FirstOrDefaultAsync(cancellationToken);

        if (deployment == null)
        {
            return new MembershipTableData(NotFound);
        }

        return deployment.ToTable(BuildKey(address));
    }

    public async Task UpdateIAmAlive(string deploymentId, SiloAddress address, DateTime iAmAliveTime, CancellationToken cancellationToken = default)
    {
        var filter = Builders<DeploymentDocument>.Filter.And(
            Builders<DeploymentDocument>.Filter.Eq(x => x.DeploymentId, deploymentId),
            Builders<DeploymentDocument>.Filter.Exists($"Members.{BuildKey(address)}", true));

        await GetCollection(cancellationToken).UpdateOneAsync(filter, Update
                .Set($"Members.{BuildKey(address)}.IAmAliveTime", LogFormatter.PrintDate(iAmAliveTime))
                .Set($"Members.{BuildKey(address)}.Timestamp", iAmAliveTime), cancellationToken: cancellationToken);
    }

    public async Task<bool> UpsertRow(string deploymentId, MembershipEntry entry, string? etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            var subDocument = MembershipBase.Create<DeploymentMembership>(entry);

            var memberKey = $"Members.{BuildKey(entry.SiloAddress)}";

            var etagCheck =
                etag == null ?
                    Filter.Not(Filter.Exists(memberKey)) :
                    Filter.Eq($"{memberKey}.Etag", etag);

            var result = await GetCollection(cancellationToken).UpdateOneAsync(
                Filter.And(
                    Filter.Eq(x => x.DeploymentId, deploymentId),
                    Filter.Eq(x => x.VersionEtag, tableVersion.VersionEtag),
                    etagCheck),
                Update
                    .Set(memberKey, subDocument)
                    .Set(x => x.Version, tableVersion.Version)
                    .Set(x => x.VersionEtag, EtagHelper.CreateNew()),
                new UpdateOptions { IsUpsert = etag is null }, cancellationToken);

            return result.MatchedCount > 0 || result.UpsertedId is not null;
        }
        catch (MongoException ex)
        {
            if (ex.IsDuplicateKey())
            {
                return false;
            }
            throw;
        }
    }

    private static string BuildKey(SiloAddress address)
    {
        return address.ToParsableString().Replace('.', '_');
    }
}
