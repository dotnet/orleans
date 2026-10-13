using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Runtime;

namespace Orleans.Providers.MongoDB.Membership.Store.MultipleDeprecated;

internal sealed class MultipleDeprecatedMembershipCollection : CollectionBase<MongoMembershipDocument>, IMongoMembershipCollection
{
    // This legacy format uses a fixed table version; modern strategies advance it atomically.
    private static readonly TableVersion tableVersion = new TableVersion(0, "0");
    private readonly string? collectionPrefix;

    public MultipleDeprecatedMembershipCollection(
        IMongoClient mongoClient,
        string databaseName,
        string? collectionPrefix,
        Action<MongoCollectionSettings>? collectionConfigurator,
        bool createShardKey)
        : base(mongoClient, databaseName, collectionConfigurator, createShardKey)
    {
        this.collectionPrefix = collectionPrefix;
    }

    protected override string CollectionName()
    {
        return $"{collectionPrefix}OrleansMembershipV2";
    }

    protected override void SetupCollection(IMongoCollection<MongoMembershipDocument> collection, CancellationToken cancellationToken)
    {
        var byDeploymentIdDefinition = Index.Ascending(x => x.DeploymentId);
        try
        {
            collection.Indexes.CreateOne(
                new CreateIndexModel<MongoMembershipDocument>(byDeploymentIdDefinition,
                    new CreateIndexOptions
                    {
                        Name = "ByDeploymentId"
                    }), cancellationToken: cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "IndexOptionsConflict")
        {
            collection.Indexes.CreateOne(new CreateIndexModel<MongoMembershipDocument>(byDeploymentIdDefinition), cancellationToken: cancellationToken);
        }
    }

    public async Task<bool> UpsertRow(string deploymentId, MembershipEntry entry, string? etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        var id = ReturnId(deploymentId, entry.SiloAddress);

        var document = MongoMembershipDocument.Create(entry, deploymentId, id);

        try
        {
            var result = await GetCollection(cancellationToken).ReplaceOneAsync(x => x.Id == id && x.Etag == etag, document,
                new ReplaceOptions { IsUpsert = etag is null }, cancellationToken);

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

    public async Task<IList<Uri>> GetGateways(string deploymentId, CancellationToken cancellationToken = default)
    {
        var entries =
            await GetCollection(cancellationToken).Find(x => x.DeploymentId == deploymentId && x.Status == (int)SiloStatus.Active && x.ProxyPort > 0)
                .ToListAsync(cancellationToken);

        return entries.Select(x => x.ToGatewayUri()).ToList();
    }

    public async Task<MembershipTableData> ReadAll(string deploymentId, CancellationToken cancellationToken = default)
    {
        var entries =
            await GetCollection(cancellationToken).Find(x => x.DeploymentId == deploymentId)
                .ToListAsync(cancellationToken);

        return ReturnMembershipTableData(entries);
    }

    public async Task<MembershipTableData> ReadRow(string deploymentId, SiloAddress address, CancellationToken cancellationToken = default)
    {
        var id = ReturnId(deploymentId, address);

        var entries =
            await GetCollection(cancellationToken).Find(x => x.Id == id)
                .ToListAsync(cancellationToken);

        return ReturnMembershipTableData(entries);
    }

    public Task UpdateIAmAlive(string deploymentId, SiloAddress address, DateTime iAmAliveTime, CancellationToken cancellationToken = default)
    {
        var id = ReturnId(deploymentId, address);

        return GetCollection(cancellationToken).UpdateOneAsync(x => x.Id == id, Update
            .Set(x => x.IAmAliveTime, LogFormatter.PrintDate(iAmAliveTime))
            .Set(x => x.Timestamp, iAmAliveTime), cancellationToken: cancellationToken);
    }

    public Task CleanupDefunctSiloEntries(string deploymentId, DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        var beforeUtc = beforeDate.UtcDateTime;

        return GetCollection(cancellationToken).DeleteManyAsync(Filter.And(
            Filter.Eq(x => x.DeploymentId, deploymentId),
            MembershipBase.DefunctBefore<MongoMembershipDocument>(beforeUtc)), cancellationToken);
    }

    public Task DeleteMembershipTableEntries(string deploymentId, CancellationToken cancellationToken = default)
    {
        return GetCollection(cancellationToken).DeleteManyAsync(x => x.DeploymentId == deploymentId, cancellationToken);
    }

    private static MembershipTableData ReturnMembershipTableData(IEnumerable<MongoMembershipDocument> membershipList)
    {
        return new MembershipTableData(membershipList.Select(x => Tuple.Create(x.ToEntry(), x.Etag)).ToList(), tableVersion);
    }

    private static string ReturnAddress(IPAddress address)
    {
        return address.MapToIPv4().ToString();
    }

    private static string ReturnId(string deploymentId, SiloAddress address)
    {
        return $"{deploymentId}@{ReturnAddress(address.Endpoint.Address)}:{address.Endpoint.Port}/{address.Generation}";
    }
}
