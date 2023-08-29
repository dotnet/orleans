using System;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Google.Cloud.Firestore;
using Orleans.Runtime;

namespace Orleans.Clustering.GoogleFirestore;

internal class OrleansSiloInstanceManager
{
    public const string INSTANCE_STATUS_CREATED = nameof(SiloStatus.Created);  //"Created";
    public const string INSTANCE_STATUS_ACTIVE = nameof(SiloStatus.Active);    //"Active";
    public const string INSTANCE_STATUS_DEAD = nameof(SiloStatus.Dead);        //"Dead";

    private readonly FirestoreDataManager _storage;
    private readonly ILogger _logger;
    private readonly string _clusterId;

    public OrleansSiloInstanceManager(string clusterId, ILoggerFactory loggerFactory, FirestoreOptions options)
    {
        this._clusterId = clusterId;
        this._logger = loggerFactory.CreateLogger($"{nameof(OrleansSiloInstanceManager)}");
        this._storage = new FirestoreDataManager(
            MembershipEntity.CLUSTER_GROUP,
            clusterId,
            options,
            loggerFactory.CreateLogger<FirestoreDataManager>());
    }

    public static async Task<OrleansSiloInstanceManager> GetManager(string clusterId, ILoggerFactory loggerFactory, FirestoreOptions options)
    {
        var manager = new OrleansSiloInstanceManager(clusterId, loggerFactory, options);
        try
        {
            await manager._storage.Initialize();
            return manager;
        }
        catch (Exception ex)
        {
            manager._logger.LogError(ex,
                "Error trying to connect to Google Firestore collection {Collection} on project {Project}", options.RootCollectionName, options.ProjectId);
            throw new OrleansException($"Error trying to connect to Google Firestore collection {options.RootCollectionName} on project {options.ProjectId}", ex);
        }
    }

    public ClusterVersionEntity CreateClusterVersionEntity(int version)
    {
        return new ClusterVersionEntity
        {
            ClusterId = this._clusterId,
            Id = this._clusterId,
            MembershipVersion = version
        };
    }

    public Task<string> RegisterSiloInstance(SiloInstanceEntity entry)
    {
        entry.Status = INSTANCE_STATUS_CREATED;
        this._logger.LogInformation((int)ErrorCode.Runtime_Error_100270, "Registering silo instance: {Data}", entry.ToString());
        return this._storage.UpsertEntity(entry);
    }

    public Task<string> UnregisterSiloInstance(SiloInstanceEntity entry)
    {
        entry.Status = INSTANCE_STATUS_DEAD;
        this._logger.LogInformation((int)ErrorCode.Runtime_Error_100271, "Unregistering silo instance: {Data}", entry.ToString());
        return this._storage.UpsertEntity(entry);
    }

    public Task<string> ActivateSiloInstance(SiloInstanceEntity entry)
    {
        this._logger.LogInformation((int)ErrorCode.Runtime_Error_100272, "Activating silo instance: {Data}", entry.ToString());
        entry.Status = INSTANCE_STATUS_ACTIVE;
        return this._storage.UpsertEntity(entry);
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

    public async Task<IList<Uri>> FindAllGatewayProxyEndpoints()
    {
        if (this._logger.IsEnabled(LogLevel.Debug)) this._logger.LogDebug((int)ErrorCode.Runtime_Error_100277, "Searching for active gateway silos for deployment {DeploymentId}.", this._clusterId);

        try
        {
            var e = await this._storage.ReadAllEntities<SiloInstanceEntity>();
            var results = await this._storage.QueryEntities<SiloInstanceEntity>(
                silo => silo
                    .WhereEqualTo(nameof(SiloInstanceEntity.Status), INSTANCE_STATUS_ACTIVE)
                    .WhereGreaterThan(nameof(SiloInstanceEntity.ProxyPort), 0)
                );

            var gatewaySiloInstances = results.Select(ConvertToGatewayUri).ToList();

            this._logger.LogInformation((int)ErrorCode.Runtime_Error_100278, "Found {GatewaySiloCount} active Gateway Silos for deployment {DeploymentId}.", gatewaySiloInstances.Count, this._clusterId);
            return gatewaySiloInstances;
        }
        catch (Exception exc)
        {
            this._logger.LogError((int)ErrorCode.Runtime_Error_100331, exc, "Error searching for active gateway silos for deployment {DeploymentId} ", this._clusterId);
            throw;
        }
    }

    internal Task<string> MergeTableEntryAsync(IDictionary<string, object?> fields, string id) => this._storage.MergeEntity(fields, id); // we merge this without checking eTags.

    internal Task<SiloInstanceEntity?> ReadSingleTableEntryAsync(string id) => this._storage.ReadEntity<SiloInstanceEntity>(id);

    internal async Task<int> DeleteTableEntries()
    {
        var entities = await this._storage.ReadAllEntities<SiloInstanceEntity>();

        if (entities.Length > 0)
        {
            await this.DeleteEntriesBatch(entities);
        }

        return entities.Length;
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        var entities = await this._storage.QueryEntities<SiloInstanceEntity>(
            silo => silo
                .WhereLessThan(nameof(SiloInstanceEntity.IAmAliveTime), beforeDate)
                .WhereNotEqualTo(nameof(SiloInstanceEntity.Status), INSTANCE_STATUS_ACTIVE)
            );

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
        var version = await this._storage.ReadEntity<ClusterVersionEntity>(this._clusterId) ?? throw new KeyNotFoundException($"Could not find cluster version entry for {this._clusterId}");
        var silo = await this._storage.ReadEntity<SiloInstanceEntity>(siloAddress.ToParsableString()) ?? throw new KeyNotFoundException($"Could not find silo entry for {siloAddress.ToParsableString()}");

        return (silo, version);
    }

    internal async Task<(SiloInstanceEntity[] Silos, ClusterVersionEntity Version)> FindAllSiloEntries()
    {
        var version = await this._storage.ReadEntity<ClusterVersionEntity>(this._clusterId) ?? throw new KeyNotFoundException($"Could not find cluster version entry for {this._clusterId}");

        var silos = await this._storage.ReadAllEntities<SiloInstanceEntity>();
        silos = silos.Where(e => e.Id != this._clusterId).ToArray(); // Exclude the cluster version entry

        return (silos, version);
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
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Unable to create cluster version entry for deployment {DeploymentId} ", this._clusterId);

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

        var result = false;

        try
        {
            result = await this._storage.ExecuteTransaction(trx =>
            {
                trx.Create(siloReference, silo);
                trx.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag!.Value));
                return Task.FromResult(true);
            });
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to insert silo entry for silo {SiloAddress} ", silo.Id);
        }
        return result;
    }

    internal async Task<bool> UpdateSiloEntryConditionally(SiloInstanceEntity silo, ClusterVersionEntity version)
    {
        var collection = this._storage.GetCollection();
        var siloReference = collection.Document(silo.Id);
        var versionReference = collection.Document(this._clusterId);

        var result = false;

        try
        {
            result = await this._storage.ExecuteTransaction(trx =>
            {
                trx.Update(siloReference, silo.GetFields(), Precondition.LastUpdated(silo.ETag!.Value));
                trx.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag!.Value));
                return Task.FromResult(true);
            });
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to update silo entry for silo {SiloAddress} ", silo.Id);
        }
        return result;
    }
}