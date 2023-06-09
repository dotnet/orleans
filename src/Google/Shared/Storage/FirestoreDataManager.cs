using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using System.Globalization;
using System.Collections.Generic;
using Grpc.Core;


#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.GoogleFirestore;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.GoogleFirestore;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.GoogleFirestore;
#elif GOOGLE_TESTS
namespace Orleans.Tests.GoogleFirestore;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.GoogleFirestore;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

internal class FirestoreDataManager
{
    public const int MAX_BATCH_ENTRIES = 500; // Batches are only allowed to have 500 operations
    private readonly FirestoreOptions _options;
    private readonly FirestoreDb _db;
    private readonly string _group;
    private readonly string _partition;
    protected readonly ILogger Logger;

    public FirestoreDataManager(string group, string partition, FirestoreOptions options, ILogger logger)
    {
        this._group = group ?? throw new ArgumentNullException(nameof(group));
        this._partition = partition ?? throw new ArgumentNullException(nameof(partition));
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this.Logger = logger ?? throw new ArgumentNullException(nameof(logger));

        this._db = !string.IsNullOrWhiteSpace(this._options.EmulatorHost)
            ? new FirestoreDbBuilder
            {
                ProjectId = this._options.ProjectId,
                EmulatorDetection = EmulatorDetection.EmulatorOrProduction
            }.Build()
            : FirestoreDb.Create(this._options.ProjectId);
    }

    /// <summary>
    /// Initialize the data manager.
    /// </summary>
    public async Task Initialize()
    {
        if (this.Logger.IsEnabled(LogLevel.Debug)) this.Logger.LogDebug("Initializing FirestoreDataManager");

        try
        {
            var group = this._db.Collection(this._options.RootCollectionName).Document(this._group);

            var snapshot = await group.GetSnapshotAsync();

            if (!snapshot.Exists)
            {
                // Create a header document to ensure the subcollection can be created afterwards
                await group.CreateAsync(new { StorageGroup = this._group });
            }
        }
        catch (RpcException ex)
        {
            if (ex.StatusCode != StatusCode.AlreadyExists)
            {
                throw;
            }
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.Initialize));
            throw;
        }
    }

    /// <summary>
    /// Clears the collection.
    /// </summary>
    public async Task ClearCollection()
    {
        var collection = this.GetCollection();
        var colSnapshot = await collection.GetSnapshotAsync();

        if (colSnapshot.Count == 0) return;

        foreach (var chunk in colSnapshot.Documents.Chunk(MAX_BATCH_ENTRIES))
        {
            var batch = this._db.StartBatch();

            foreach (var doc in chunk)
            {
                batch.Delete(doc.Reference);
            }

            await batch.CommitAsync();
        }
    }

    /// <summary>
    /// Create a entity if it doesn't exist, otherwise it will throw
    /// </summary>
    /// <param name="entity">The entity</param>
    /// <returns>The entity's eTag</returns>
    public async Task<string> CreateEntity<TEntity>(TEntity entity) where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Creating entity {id} on collection {collection}", entity.Id, this._partition);

        try
        {
            ValidateEntity(entity);

            var docRef = collection.Document(entity.Id);
            var result = await docRef.CreateAsync(entity);

            return Utils.FormatTimestamp(result.UpdateTime);
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.CreateEntity));
            throw;
        }
    }

    public async Task<string> UpsertEntity<TEntity>(TEntity entity) where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Upserting entity {id} on collection {collection}", entity.Id, this._partition);

        try
        {
            ValidateEntity(entity);

            var docRef = collection.Document(entity.Id);

            var result = await docRef.SetAsync(entity, SetOptions.MergeAll);
            return Utils.FormatTimestamp(result.UpdateTime);
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.UpsertEntity));
            throw;
        }
    }

    public async Task<string> MergeEntity(IDictionary<string, object?> fields, string id)
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Merging entity {id} on collection {collection}", id, this._partition);

        try
        {
            var docRef = collection.Document(id);

            var result = await docRef.SetAsync(fields, SetOptions.MergeAll);
            return Utils.FormatTimestamp(result.UpdateTime);
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.MergeEntity));
            throw;
        }
    }

    /// <summary>
    /// Update an entity.
    /// </summary>
    /// <param name="entity">The entity</param>
    /// <returns>The entity's eTag</returns>
    public async Task<string> Update<TEntity>(TEntity entity) where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Merging entity {id} on collection {collection}", entity.Id, this._partition);

        try
        {
            ValidateEntity(entity, true);

            var docRef = collection.Document(entity.Id);

            var result = await docRef.UpdateAsync(entity.GetFields(), Precondition.LastUpdated(Timestamp.FromDateTimeOffset(entity.ETag)));
            return Utils.FormatTimestamp(result.UpdateTime);
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.Update));
            throw;
        }
    }

    /// <summary>
    /// Delete an entity.
    /// </summary>
    /// <param name="id">The entity's id</param>
    /// <param name="eTag">The entity's eTag</param>
    public Task DeleteEntity(string id, string eTag) => this.DeleteEntity(id, Utils.ParseTimestamp(eTag));

    /// <summary>
    /// Delete an entity.
    /// </summary>
    /// <param name="id">The entity's id</param>
    /// <param name="eTag">The entity's eTag</param>
    public async Task DeleteEntity(string id, DateTimeOffset? eTag = null)
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Deleting entity {id} on collection {collection}", id, this._partition);

        try
        {
            var docRef = collection.Document(id);

            if (eTag.HasValue)
            {
                await docRef.DeleteAsync(Precondition.LastUpdated(Timestamp.FromDateTimeOffset(eTag.Value)));
            }
            else
            {
                await docRef.DeleteAsync(Precondition.MustExist);
            }
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.DeleteEntity));
            throw;
        }
    }

    /// <summary>
    /// Read an entity.
    /// </summary>
    /// <param name="id">The entity's id</param>
    /// <returns>The entity or null of not exist</returns>
    public async Task<TEntity?> ReadEntity<TEntity>(string id) where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Reading entity {id} on collection {collection}", id, this._partition);

        try
        {
            var docRef = collection.Document(id);

            var snapshot = await docRef.GetSnapshotAsync();

            if (!snapshot.Exists)
            {
                if (this.Logger.IsEnabled(LogLevel.Debug)) this.Logger.LogTrace("Entity {id} not found on collection {collection}", id, this._partition);

                return null;
            }

            var entity = snapshot.ConvertTo<TEntity>();

            return entity;
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.ReadEntity));
            throw;
        }
    }

    /// <summary>
    /// Read all entities.
    /// </summary>
    /// <returns>The entities</returns>
    public async Task<TEntity[]> ReadAllEntities<TEntity>() where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Reading all entities on collection {collection}", this._partition);

        try
        {
            var snapshot = await collection.GetSnapshotAsync();

            if (snapshot.Count == 0)
            {
                if (this.Logger.IsEnabled(LogLevel.Debug)) this.Logger.LogTrace("No entities found on collection {collection}", this._partition);

                return Array.Empty<TEntity>();
            }

            return snapshot.Documents.Select(d => d.ConvertTo<TEntity>()).ToArray();
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.ReadAllEntities));
            throw;
        }
    }

    /// <summary>
    /// Delete entities in a partition.
    /// </summary>
    /// <param name="entities">Entities to be deleted</param>
    public async Task DeleteEntities<TEntity>(TEntity[] entities) where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();

        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Deleting entities on collection {collection}", this._partition);

        if (entities.Length == 0) return;

        try
        {
            if (entities.Length > MAX_BATCH_ENTRIES) throw new ArgumentOutOfRangeException($"Batch operation limit exceeded ({MAX_BATCH_ENTRIES})");

            var batch = this._db.StartBatch();

            foreach (var entity in entities)
            {
                ValidateEntity(entity, true);

                var docRef = collection.Document(entity.Id);

                batch.Delete(docRef, Precondition.LastUpdated(Timestamp.FromDateTimeOffset(entity.ETag)));
            }

            await batch.CommitAsync();
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.DeleteEntities));
            throw;
        }
    }

    /// <summary>
    /// Query entities.
    /// </summary>
    /// <param name="query">The query filter</param>
    /// <returns>An array of entities</returns>
    public async Task<TEntity[]> QueryEntities<TEntity>(Func<CollectionReference, Query> query) where TEntity : FirestoreEntity, new()
    {
        var collection = this.GetCollection();
        if (this.Logger.IsEnabled(LogLevel.Trace)) this.Logger.LogTrace("Querying entities on collection {collection}", this._partition);

        try
        {
            var snapshot = await query(collection).GetSnapshotAsync();

            if (snapshot.Count == 0)
            {
                if (this.Logger.IsEnabled(LogLevel.Debug)) this.Logger.LogTrace("No entities found on collection {collection}", this._partition);

                return Array.Empty<TEntity>();
            }

            return snapshot.Documents.Select(d => d.ConvertTo<TEntity>()).ToArray();
        }
        catch (Exception ex)
        {
            this.LogError(ex, nameof(this.QueryEntities));
            throw;
        }
    }

    public Task ExecuteTransaction(Func<Transaction, Task> transactionScope) => this._db.RunTransactionAsync(transactionScope);

    public Task<TEntity> ExecuteTransaction<TEntity>(Func<Transaction, Task<TEntity>> transactionScope) => this._db.RunTransactionAsync(transactionScope);

    private static void ValidateEntity<TEntity>(TEntity entity, bool updating = false) where TEntity : FirestoreEntity, new()
    {
        if (entity.Id == default) throw new InvalidOperationException("Id is required to create or update an entity");
        if (updating)
        {
            if (entity.ETag == default) throw new InvalidOperationException("ETag is required to update an entity");
            if (entity.ETag < DateTimeOffset.UnixEpoch) throw new InvalidOperationException("ETag must be greater than 1970-01-01T00:00:00Z");
        }
    }

    public CollectionReference GetCollection() =>
        this._db.Collection($"{this._options.RootCollectionName}").Document(this._group).Collection(this._partition);

    private void LogError(Exception ex, string operation) =>
        this.Logger.LogError(ex, "Error on {operation} on collection {collection}", operation, this._partition);
}