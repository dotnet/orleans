using System;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Google.Cloud.Firestore;
using Grpc.Core;
using Orleans.Runtime;

namespace Orleans.Clustering.GoogleFirestore;

internal partial class OrleansSiloInstanceManager
{
    private const string ClusterGroup = "Cluster";
    private readonly FirestoreDataManager _storage;
    private readonly ILogger _logger;
    private readonly string _clusterId;

    private OrleansSiloInstanceManager(string clusterId, ILoggerFactory loggerFactory, FirestoreOptions options)
    {
        this._clusterId = clusterId;
        this._logger = loggerFactory.CreateLogger<OrleansSiloInstanceManager>();
        this._storage = new FirestoreDataManager(
            ClusterGroup,
            clusterId,
            options,
            loggerFactory.CreateLogger<FirestoreDataManager>());
    }

    internal static async Task<OrleansSiloInstanceManager> GetManager(string clusterId, ILoggerFactory loggerFactory, FirestoreOptions options)
    {
        var manager = new OrleansSiloInstanceManager(clusterId, loggerFactory, options);
        try
        {
            await manager._storage.Initialize();
            return manager;
        }
        catch (Exception ex)
        {
            manager.LogErrorConnecting(ex, options.RootCollectionName, options.ProjectId);
            throw;
        }
    }

    internal ClusterVersionEntity CreateClusterVersionEntity(int version)
    {
        return new ClusterVersionEntity
        {
            ClusterId = this._clusterId,
            Id = this._clusterId,
            MembershipVersion = version
        };
    }

    /// <summary>
    /// Represent a silo instance entry in the gateway URI format.
    /// </summary>
    /// <param name="gateway">The input silo instance</param>
    /// <returns>Uri in the gateway format</returns>
    private static Uri ConvertToGatewayUri(SiloInstanceEntity gateway)
    {
        var address = SiloAddress.New(IPAddress.Parse(gateway.Address), gateway.ProxyPort, gateway.Generation);
        return address.ToGatewayUri();
    }

    internal async Task<IList<Uri>> FindAllGatewayProxyEndpoints()
    {
        LogSearchingForGateways(this._clusterId);

        try
        {
            var results = await this._storage.QueryEntities<SiloInstanceEntity>(
                silo => silo
                    .WhereEqualTo(nameof(SiloInstanceEntity.Status), (int)SiloStatus.Active)
                );

            var gatewaySiloInstances = results
                .Where(silo => silo.ProxyPort > 0)
                .Select(ConvertToGatewayUri)
                .ToList();

            LogFoundGateways(gatewaySiloInstances.Count, this._clusterId);
            return gatewaySiloInstances;
        }
        catch (Exception exc)
        {
            LogErrorSearchingForGateways(exc, this._clusterId);
            throw;
        }
    }

    internal Task<string> MergeTableEntryAsync(IDictionary<string, object?> fields, string id) => this._storage.MergeEntity(fields, id); // we merge this without checking eTags.

    internal async Task<int> DeleteTableEntries()
    {
        return await this._storage.ClearCollection();
    }

    internal async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        var entities = await this._storage.ReadAllEntities<SiloInstanceEntity>();
        entities = entities
            .Where(entity => entity.Id != this._clusterId)
            .Where(entity => entity.Status != (int)SiloStatus.Active)
            .Where(entity => GetEffectiveUpdateTime(entity) < beforeDate)
            .ToArray();

        if (entities.Length > 0)
        {
            await this.DeleteEntriesBatch(entities);
        }
    }

    private async Task DeleteEntriesBatch(SiloInstanceEntity[] entities)
    {
        entities = entities.Where(e => e.Id != this._clusterId).ToArray(); // Don't delete the cluster version entry

        if (entities.Length < FirestoreDataManager.MAX_BATCH_ENTRIES)
        {
            await this._storage.DeleteEntities(entities);
        }
        else
        {
            var tasks = new List<Task>();
            var batch = new List<SiloInstanceEntity>(FirestoreDataManager.MAX_BATCH_ENTRIES);
            foreach (var entity in entities)
            {
                batch.Add(entity);
                if (batch.Count == FirestoreDataManager.MAX_BATCH_ENTRIES)
                {
                    tasks.Add(this._storage.DeleteEntities(batch.ToArray()));
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                tasks.Add(this._storage.DeleteEntities(batch.ToArray()));
            }

            await Task.WhenAll(tasks);
        }
    }

    internal async Task<(SiloInstanceEntity Silo, ClusterVersionEntity Version)> FindSiloAndVersionEntities(SiloAddress siloAddress)
    {
        var collection = this._storage.GetCollection();
        return await this._storage.ExecuteTransaction(async transaction =>
        {
            var versionSnapshot = await transaction.GetSnapshotAsync(collection.Document(this._clusterId));
            var siloSnapshot = await transaction.GetSnapshotAsync(collection.Document(siloAddress.ToParsableString()));
            if (!versionSnapshot.Exists) throw new KeyNotFoundException($"Could not find cluster version entry for {this._clusterId}");
            if (!siloSnapshot.Exists) throw new KeyNotFoundException($"Could not find silo entry for {siloAddress.ToParsableString()}");
            return (siloSnapshot.ConvertTo<SiloInstanceEntity>(), versionSnapshot.ConvertTo<ClusterVersionEntity>());
        });
    }

    internal async Task<(SiloInstanceEntity[] Silos, ClusterVersionEntity Version)> FindAllSiloEntries()
    {
        var collection = this._storage.GetCollection();
        return await this._storage.ExecuteTransaction(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(collection);
            var versionSnapshot = snapshot.Documents.SingleOrDefault(document => document.Id == this._clusterId)
                ?? throw new KeyNotFoundException($"Could not find cluster version entry for {this._clusterId}");
            var silos = snapshot.Documents
                .Where(document => document.Id != this._clusterId)
                .Select(document => document.ConvertTo<SiloInstanceEntity>())
                .ToArray();
            return (silos, versionSnapshot.ConvertTo<ClusterVersionEntity>());
        });
    }

    /// <summary>
    /// Insert (create new) row entry
    /// </summary>
    internal async Task<bool> TryCreateTableVersionEntryAsync()
    {
        try
        {
            var version = await this._storage.ReadEntity<ClusterVersionEntity>(this._clusterId);
            if (version is not null) return false;

            var entity = CreateClusterVersionEntity(0);
            await this._storage.CreateEntity(entity);

            return true;
        }
        catch (RpcException exc) when (exc.StatusCode == StatusCode.AlreadyExists)
        {
            return false;
        }
    }

    /// <summary>
    /// Insert (create new) row entry
    /// </summary>
    /// <param name="silo">Silo Entity to be written</param>
    /// <param name="version">Version row to update</param>
    internal async Task<bool> InsertSiloEntryConditionally(SiloInstanceEntity silo, ClusterVersionEntity version)
    {
        var collection = this._storage.GetCollection();
        var siloReference = collection.Document(silo.Id);
        var versionReference = collection.Document(this._clusterId);

        try
        {
            return await this._storage.ExecuteTransaction(trx =>
            {
                trx.Create(siloReference, silo);
                trx.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag!.Value));
                return Task.FromResult(true);
            });
        }
        catch (RpcException ex) when (IsContention(ex))
        {
            return false;
        }
    }

    internal async Task<bool> UpdateSiloEntryConditionally(SiloInstanceEntity silo, ClusterVersionEntity version)
    {
        var collection = this._storage.GetCollection();
        var siloReference = collection.Document(silo.Id);
        var versionReference = collection.Document(this._clusterId);

        try
        {
            return await this._storage.ExecuteTransaction(trx =>
            {
                trx.Update(siloReference, silo.GetFields(), Precondition.LastUpdated(silo.ETag!.Value));
                trx.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag!.Value));
                return Task.FromResult(true);
            });
        }
        catch (RpcException ex) when (IsContention(ex))
        {
            return false;
        }
    }

    private static bool IsContention(RpcException exception) =>
        exception.StatusCode is StatusCode.Aborted or StatusCode.AlreadyExists or StatusCode.FailedPrecondition or StatusCode.NotFound;

    private static DateTimeOffset GetEffectiveUpdateTime(SiloInstanceEntity entity)
    {
        var result = entity.StartTime > entity.IAmAliveTime ? entity.StartTime : entity.IAmAliveTime;
        if (entity.SuspectingSilos is { Count: > 0 })
        {
            var latestSuspectTime = entity.SuspectingSilos.Values.Max();
            if (latestSuspectTime > result)
            {
                result = latestSuspectTime;
            }
        }

        return result;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error trying to connect to Google Firestore collection {Collection} on project {Project}")]
    private partial void LogErrorConnecting(Exception exception, string collection, string project);

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100277,
        Level = LogLevel.Debug,
        Message = "Searching for active gateway silos for deployment {DeploymentId}.")]
    private partial void LogSearchingForGateways(string deploymentId);

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100278,
        Level = LogLevel.Debug,
        Message = "Found {GatewaySiloCount} active Gateway Silos for deployment {DeploymentId}.")]
    private partial void LogFoundGateways(int gatewaySiloCount, string deploymentId);

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100331,
        Level = LogLevel.Error,
        Message = "Error searching for active gateway silos for deployment {DeploymentId}")]
    private partial void LogErrorSearchingForGateways(Exception exception, string deploymentId);
}