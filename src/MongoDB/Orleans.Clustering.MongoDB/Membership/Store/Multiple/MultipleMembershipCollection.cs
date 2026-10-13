using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Runtime;

namespace Orleans.Providers.MongoDB.Membership.Store.Multiple;

internal sealed class MultipleMembershipCollection : CollectionBase<MongoMembershipDocument>, IMongoMembershipCollection
{
    private static readonly TransactionOptions MembershipTransactionOptions =
        new(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority);
    private readonly TableVersionCollection tableVersionCollection;
    private readonly string? collectionPrefix;

    public MultipleMembershipCollection(
        IMongoClient mongoClient,
        string databaseName,
        string? collectionPrefix,
        Action<MongoCollectionSettings>? collectionConfigurator,
        bool createShardKey)
        : base(mongoClient, databaseName, collectionConfigurator, createShardKey)
    {
        this.collectionPrefix = collectionPrefix;

        tableVersionCollection = new TableVersionCollection(mongoClient, databaseName, collectionPrefix, collectionConfigurator, createShardKey);
    }

    protected override string CollectionName()
    {
        return $"{collectionPrefix}OrleansMembershipV3";
    }

    protected override void SetupCollection(IMongoCollection<MongoMembershipDocument> collection, CancellationToken cancellationToken)
    {
        var byDeploymentIdDefinition = Index.Ascending(x => x.DeploymentId);

        collection.Indexes.CreateOne(
            new CreateIndexModel<MongoMembershipDocument>(byDeploymentIdDefinition,
                new CreateIndexOptions
                {
                    Name = "ByDeploymentId"
                }), cancellationToken: cancellationToken);
    }

    public async Task<bool> UpsertRow(string deploymentId, MembershipEntry entry, string? etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        using var session = await Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync(async (sessionHandle, ct) =>
        {
            var hasUpsertedTable = await tableVersionCollection.UpsertAsync(sessionHandle, tableVersion, deploymentId, ct);

            if (!hasUpsertedTable)
            {
                await sessionHandle.AbortTransactionAsync(ct);
                return false;
            }

            var hasUpsertedMember = await UpsertRowAsync(sessionHandle, deploymentId, entry, etag, ct);

            if (!hasUpsertedMember)
            {
                await sessionHandle.AbortTransactionAsync(ct);
            }

            return hasUpsertedMember;
        }, MembershipTransactionOptions, cancellationToken);
    }

    private async Task<bool> UpsertRowAsync(IClientSessionHandle session, string deploymentId, MembershipEntry entry, string? etag, CancellationToken cancellationToken)
    {
        var id = ReturnId(deploymentId, entry.SiloAddress);

        var document = MongoMembershipDocument.Create(entry, deploymentId, id);

        try
        {
            var result = await GetCollection(cancellationToken).ReplaceOneAsync(session, x => x.Id == id && x.Etag == etag, document,
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
        using var session = await Client.StartSessionAsync(cancellationToken: cancellationToken);

        // Rows and the version must describe the same topology snapshot.
        return await session.WithTransactionAsync(async (sessionHandle, ct) =>
        {
            var tableVersion = await tableVersionCollection.GetTableVersionAsync(sessionHandle, deploymentId, ct);
            var entries = await GetCollection(ct).Find(sessionHandle, x => x.DeploymentId == deploymentId).ToListAsync(ct);
            return ReturnMembershipTableData(entries, tableVersion);
        }, MembershipTransactionOptions, cancellationToken);
    }

    public async Task<MembershipTableData> ReadRow(string deploymentId, SiloAddress address, CancellationToken cancellationToken = default)
    {
        using var session = await Client.StartSessionAsync(cancellationToken: cancellationToken);

        var id = ReturnId(deploymentId, address);
        return await session.WithTransactionAsync(async (sessionHandle, ct) =>
        {
            var tableVersion = await tableVersionCollection.GetTableVersionAsync(sessionHandle, deploymentId, ct);
            var entries = await GetCollection(ct).Find(sessionHandle, x => x.Id == id).ToListAsync(ct);
            return ReturnMembershipTableData(entries, tableVersion);
        }, MembershipTransactionOptions, cancellationToken);

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

    public async Task DeleteMembershipTableEntries(string deploymentId, CancellationToken cancellationToken = default)
    {
        using var session = await Client.StartSessionAsync(cancellationToken: cancellationToken);

        await session.WithTransactionAsync(async (sessionHandle, ct) =>
        {
            await GetCollection(ct).DeleteManyAsync(sessionHandle, x => x.DeploymentId == deploymentId, cancellationToken: ct);
            await tableVersionCollection.DeleteAsync(sessionHandle, deploymentId, ct);
            return true;
        }, MembershipTransactionOptions, cancellationToken);
    }

    private static MembershipTableData ReturnMembershipTableData(IEnumerable<MongoMembershipDocument> membershipList, TableVersion tableVersion)
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
