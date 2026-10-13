using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Orleans.Reminders.MongoDB;
using Orleans.Runtime;


namespace Orleans.Providers.MongoDB.Reminders.Store;

internal class MongoReminderCollection : CollectionBase<MongoReminderDocument>
{
    private readonly string serviceId;
    private readonly string? collectionPrefix;
    private readonly bool removeAllLegacyIndexes;
    private readonly bool createIndexes;

    public MongoReminderCollection(IMongoClient mongoClient,
        string databaseName,
        string? collectionPrefix,
        Action<MongoCollectionSettings>? collectionConfigurator,
        bool createShardKey,
        bool createIndexes,
        bool removeAllLegacyIndexes,
        string serviceId)
        : base(mongoClient, databaseName, collectionConfigurator, createShardKey)
    {
        this.serviceId = serviceId;
        this.collectionPrefix = collectionPrefix;
        this.removeAllLegacyIndexes = removeAllLegacyIndexes;
        this.createIndexes = createIndexes;
    }

    protected override string CollectionName()
    {
        return collectionPrefix + "OrleansReminderV2";
    }

    protected override void SetupCollection(IMongoCollection<MongoReminderDocument> collection, CancellationToken cancellationToken)
    {
        if (!createIndexes)
        {
            return;
        }

        var byGrainHashDefinition =
            Index
                .Ascending(x => x.ServiceId)
                .Ascending(x => x.GrainHash);
        try
        {
            collection.Indexes.CreateOne(
                new CreateIndexModel<MongoReminderDocument>(byGrainHashDefinition,
                    new CreateIndexOptions
                    {
                        Name = "ByGrainHash"
                    }), cancellationToken: cancellationToken);
        }
        catch (MongoCommandException ex)
        {
            if (!ex.IsDuplicateIndex())
            {
                throw;
            }

            // Preserve an equivalent index installed under an administrator-selected name.
        }

        List<string> indexesToRemove =
        [
            // ByHash definitions.
            "ByHash",
#pragma warning disable CS0618 // Type or member is obsolete
            $"{collection.GetFieldName(r => r.IsDeleted!)}_1"
            + $"_{collection.GetFieldName(r => r.ServiceId)}_1"
            + $"_{collection.GetFieldName(r => r.GrainHash)}_1"
#pragma warning restore CS0618 // Type or member is obsolete
        ];

        if (removeAllLegacyIndexes)
        {
            // these indexes are safe to keep and redundant.
            // However, if a rolling deployment is made with silos with Orleans.Providers.MongoDB on version <=9.3.0,
            // there is a high chance of collection scans impacting the full cluster.
            // See: https://github.com/OrleansContrib/Orleans.Providers.MongoDB/pull/154
            indexesToRemove.AddRange([
                // ByName definition
                "ByName",
#pragma warning disable CS0618 // Type or member is obsolete
                $"{collection.GetFieldName(r => r.IsDeleted!)}_1"
                + $"_{collection.GetFieldName(r => r.ServiceId)}_1"
                + $"_{collection.GetFieldName(r => r.GrainId)}_1"
                + $"_{collection.GetFieldName(r => r.ReminderName)}_1",
#pragma warning restore CS0618 // Type or member is obsolete
            ]);
        }

        foreach (var indexToRemove in indexesToRemove)
        {
            try
            {
                collection.Indexes.DropOne(indexToRemove, cancellationToken);
            }
            catch (MongoCommandException exception) when (exception.Code == 27)
            {
                // IndexNotFound: the legacy index has already been removed.
            }
        }
    }

    public virtual async Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
    {
        // (begin) is beginning exclusive of hash
        // [end] is the stop point, inclusive of hash
        var filter = beginHash < endHash
            ? Filter.And(
                Filter.Eq(x => x.ServiceId, serviceId),
                //       (begin)>>>>>>[end]
                Filter.Gt(x => x.GrainHash, beginHash),
                Filter.Lte(x => x.GrainHash, endHash)
            )
            : Filter.And(
                Filter.Eq(x => x.ServiceId, serviceId),
                Filter.Or( // >>>>>>[end]         (begin)>>>>>>>
                    Filter.Lte(x => x.GrainHash, endHash),
                    Filter.Gt(x => x.GrainHash, beginHash)
                )
            );
        var reminders = await Collection.Find(filter).ToListAsync();

        return new ReminderTableData(reminders.Select(x => x.ToEntry()));
    }

    public virtual async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        var id = ReturnId(serviceId, grainId, reminderName);
        var reminder =
            await Collection.Find(Filter.Eq(x => x.Id, id))
                .FirstOrDefaultAsync();

        return reminder?.ToEntry();
    }

    public virtual async Task<ReminderTableData> ReadRow(GrainId grainId)
    {
        var reminders =
            await Collection.Find(
                Filter.And(
                    Filter.Eq(x => x.ServiceId, serviceId),
                    Filter.Eq(x => x.GrainHash, grainId.GetUniformHashCode()),
                    Filter.Eq(x => x.GrainId, grainId.ToString())
                )
            ).ToListAsync();

        return new ReminderTableData(reminders.Select(x => x.ToEntry()));
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        var id = ReturnId(serviceId, grainId, reminderName);

        var deleteResult = await Collection.DeleteOneAsync(
            Filter.And(
                Filter.Eq(x => x.Id, id),
                Filter.Eq(x => x.Etag, eTag))
        );
        return deleteResult.DeletedCount > 0;
    }

    public virtual Task RemoveRows()
    {
        // note: only used and called by the test harness
        return Collection.DeleteManyAsync(r => r.ServiceId == serviceId);
    }

    public virtual async Task<string> UpsertRow(ReminderEntry entry)
    {
        var id = ReturnId(serviceId, entry.GrainId, entry.ReminderName);
        var document = MongoReminderDocument.Create(id, serviceId, entry, Guid.NewGuid().ToString());

        await Collection.UpdateOneAsync(
            Filter.Eq(x => x.Id, id),
            Update
                .SetOnInsert(x => x.ServiceId, document.ServiceId)
                .SetOnInsert(x => x.GrainId, document.GrainId)
                .SetOnInsert(x => x.ReminderName, document.ReminderName)
                .Set(x => x.GrainHash, document.GrainHash)
                .Set(x => x.Etag, document.Etag)
                .Set(x => x.Period, document.Period)
                .Set(x => x.StartAt, document.StartAt),
            Upsert);

        return entry.ETag = document.Etag;
    }

    private static string ReturnId(string serviceId, GrainId grainId, string reminderName)
    {
        return $"{serviceId}_{grainId}_{reminderName}";
    }
}
