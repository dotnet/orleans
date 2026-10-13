using System;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace Orleans.Providers.MongoDB.StorageProviders;

internal sealed class MongoGrainStorageCollection : CollectionBase<BsonDocument>
{
    private const string FieldId = "_id";
    private const string FieldDoc = "_doc";
    private const string FieldEtag = "_etag";
    private readonly string collectionName;
    private readonly IGrainStateSerializer serializer;
    private readonly IActivatorProvider activatorProvider;
    private readonly GrainStorageKeyGenerator keyGenerator;

    public MongoGrainStorageCollection(
        IMongoClient mongoClient,
        string databaseName,
        string collectionName,
        Action<MongoCollectionSettings>? collectionConfigurator,
        bool createShardKey,
        IGrainStateSerializer serializer,
        IActivatorProvider activatorProvider,
        GrainStorageKeyGenerator keyGenerator)
        : base(mongoClient, databaseName, collectionConfigurator, createShardKey)
    {
        this.collectionName = collectionName;
        this.serializer = serializer;
        this.activatorProvider = activatorProvider;
        this.keyGenerator = keyGenerator;
    }

    protected override string CollectionName()
    {
        return collectionName;
    }

    public async Task ReadAsync<T>(GrainId grainId, IGrainState<T> grainState)
    {
        var grainKey = keyGenerator(grainId);

        var existing =
            await Collection.Find(Filter.Eq(FieldId, grainKey))
                .FirstOrDefaultAsync();

        if (existing != null)
        {
            grainState.RecordExists = true;

            if (existing.Contains(FieldDoc))
            {
                grainState.ETag = existing[FieldEtag].AsString;

                grainState.State = serializer.Deserialize<T>(existing[FieldDoc]);
            }
            else
            {
                grainState.ETag = null;
                existing.Remove(FieldId);

                grainState.State = serializer.Deserialize<T>(existing);
            }
        }
        else
        {
            grainState.State = activatorProvider.GetActivator<T>().Create();
            grainState.RecordExists = false;
            grainState.ETag = null;
        }
    }

    public async Task WriteAsync<T>(GrainId grainId, IGrainState<T> grainState)
    {
        var grainKey = keyGenerator(grainId);

        var newData = serializer.Serialize(grainState.State);

        var etag = grainState.ETag;

        var newETag = Guid.NewGuid().ToString();

        try
        {
            var filter = Filter.Eq(FieldId, grainKey);
            if (etag != "*")
            {
                filter &= Filter.Eq<BsonValue>(FieldEtag, string.IsNullOrEmpty(etag) ? BsonNull.Value : new BsonString(etag));
            }

            var result = await Collection.UpdateOneAsync(
                filter,
                Update
                    .Set(FieldEtag, newETag)
                    .Set(FieldDoc, newData),
                new UpdateOptions { IsUpsert = string.IsNullOrEmpty(etag) || etag == "*" });

            if (result.MatchedCount == 0 && result.UpsertedId is null)
            {
                await ThrowForOtherEtag(grainKey, etag);
            }
        }
        catch (MongoException ex) when (ex.IsDuplicateKey())
        {
            await ThrowForOtherEtag(grainKey, etag, ex);
        }

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearAsync<T>(GrainId grainId, IGrainState<T> grainState)
    {
        var grainKey = keyGenerator(grainId);
        var etag = grainState.ETag;
        var filter = Filter.Eq(FieldId, grainKey);
        if (etag != "*")
        {
            filter &= Filter.Eq<BsonValue>(FieldEtag, string.IsNullOrEmpty(etag) ? BsonNull.Value : new BsonString(etag));
        }

        var result = await Collection.DeleteOneAsync(filter);
        if (result.DeletedCount == 0)
        {
            var existing = await Collection.Find(Filter.Eq(FieldId, grainKey))
                .Project<BsonDocument>(Project.Include(FieldEtag)).FirstOrDefaultAsync();
            if (existing is not null)
            {
                var storedEtag = existing.GetValue(FieldEtag, BsonNull.Value);
                throw new InconsistentStateException(
                    "MongoDB grain state ETag mismatch during clear.",
                    storedEtag.IsBsonNull ? null : storedEtag.AsString,
                    etag);
            }
        }

        grainState.State = activatorProvider.GetActivator<T>().Create();
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private async Task ThrowForOtherEtag(string key, string? etag, Exception? ex = null)
    {
        var existingEtag =
            await Collection.Find(Filter.Eq(FieldId, key))
                .Project<BsonDocument>(Project.Exclude(FieldDoc)).FirstOrDefaultAsync();

        var storedEtag = existingEtag?.GetValue(FieldEtag, BsonNull.Value);
        throw new InconsistentStateException(
            "MongoDB grain state ETag mismatch during write.",
            storedEtag is null || storedEtag.IsBsonNull ? null : storedEtag.AsString,
            etag,
            ex);
    }
}
