using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Providers.MongoDB.Membership.Store;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Runtime;
using Orleans.Configuration;
using MongoDB.Driver;


namespace Orleans.Providers.MongoDB.Membership;

/// <summary>
/// Stores Orleans cluster membership and table versions in MongoDB.
/// </summary>
/// <remarks>
/// Cancellation-aware methods propagate cancellation to MongoDB operations and transactions.
/// </remarks>
public sealed class MongoMembershipTable : IMembershipTable
{
    private readonly IMongoClient mongoClient;
    private readonly ILogger<MongoMembershipTable> logger;
    private readonly MongoDBMembershipTableOptions options;
    private readonly string clusterId;
    private IMongoMembershipCollection membershipCollection = null!;

    /// <summary>
    /// Initializes the provider using the configured client, database, and cluster identity.
    /// </summary>
    public MongoMembershipTable(
        IMongoClientFactory mongoClientFactory,
        ILogger<MongoMembershipTable> logger,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<MongoDBMembershipTableOptions> options)
    {
        this.mongoClient = mongoClientFactory.Create(options.Value, "Membership");
        this.logger = logger;
        this.options = options.Value;
        this.clusterId = clusterOptions.Value.ClusterId;
    }

    /// <inheritdoc />
    [Obsolete("Use InitializeMembershipTableAsync instead.")]
    public Task InitializeMembershipTable(bool tryInitTableVersion)
        => InitializeMembershipTableAsync(tryInitTableVersion);

    /// <inheritdoc />
    public Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        membershipCollection = Factory.CreateCollection(mongoClient, options, options.Strategy);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
    public Task DeleteMembershipTableEntries(string deploymentId)
        => DeleteMembershipTableEntriesAsync(deploymentId);

    /// <inheritdoc />
    public Task DeleteMembershipTableEntriesAsync(string deploymentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(DeleteMembershipTableEntries), () =>
        {
            return membershipCollection.DeleteMembershipTableEntries(deploymentId, cancellationToken);
        });
    }

    /// <inheritdoc />
    [Obsolete("Use ReadAllAsync and MembershipTableData.TryGet instead.")]
    public Task<MembershipTableData> ReadRow(SiloAddress key)
        => ReadRowAsync(key);

    /// <inheritdoc />
    [Obsolete("Use ReadAllAsync and MembershipTableData.TryGet instead.")]
    public Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(ReadRow), () =>
        {
            return membershipCollection.ReadRow(clusterId, key, cancellationToken);
        });
    }

    /// <inheritdoc />
    [Obsolete("Use ReadAllAsync instead.")]
    public Task<MembershipTableData> ReadAll()
        => ReadAllAsync();

    /// <inheritdoc />
    public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(ReadAll), () =>
        {
            return membershipCollection.ReadAll(clusterId, cancellationToken);
        });
    }

    /// <inheritdoc />
    [Obsolete("Use InsertRowAsync instead.")]
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        => InsertRowAsync(entry, tableVersion);

    /// <inheritdoc />
    public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(InsertRow), () =>
        {
            return membershipCollection.UpsertRow(clusterId, entry, null, tableVersion, cancellationToken);
        });
    }

    /// <inheritdoc />
    [Obsolete("Use UpdateRowAsync instead.")]
    public Task<bool> UpdateRow(MembershipEntry entry, string? etag, TableVersion tableVersion)
        => UpdateRowAsync(entry, etag, tableVersion);

    /// <inheritdoc />
    public Task<bool> UpdateRowAsync(MembershipEntry entry, string? etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(UpdateRow), () =>
        {
            return membershipCollection.UpsertRow(clusterId, entry, etag, tableVersion, cancellationToken);
        });
    }

    /// <inheritdoc />
    [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        => CleanupDefunctSiloEntriesAsync(beforeDate);

    /// <inheritdoc />
    public Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(CleanupDefunctSiloEntries), () =>
        {
            return membershipCollection.CleanupDefunctSiloEntries(clusterId, beforeDate, cancellationToken);
        });
    }

    /// <inheritdoc />
    [Obsolete("Use UpdateIAmAliveAsync instead.")]
    public Task UpdateIAmAlive(MembershipEntry entry)
        => UpdateIAmAliveAsync(entry);

    /// <inheritdoc />
    public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(UpdateIAmAlive), () =>
        {
            return membershipCollection.UpdateIAmAlive(clusterId,
                entry.SiloAddress,
                entry.IAmAliveTime,
                cancellationToken);
        });
    }

    private Task DoAndLog(string actionName, Func<Task> action)
    {
        return DoAndLog(actionName, async () =>
        {
            await action();

            return true;
        });
    }

    private async Task<T> DoAndLog<T>(string actionName, Func<Task<T>> action)
    {
        logger.LogDebug($"{nameof(MongoMembershipTable)}.{actionName} called.");

        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogError((int)MongoProviderErrorCode.MembershipTable_Operations, ex, $"{nameof(MongoMembershipTable)}.{actionName} failed. Exception={ex.Message}");

            throw;
        }
    }
}
