using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
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

    public async Task ReadAsync<T>(GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        var grainKey = keyGenerator(grainId);

        var existing =
            await GetCollection(cancellationToken).Find(Filter.Eq(FieldId, grainKey))
                .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            T state;
            string? etag;

            if (existing.Contains(FieldDoc))
            {
                etag = existing[FieldEtag].AsString;
                state = serializer.Deserialize<T>(existing[FieldDoc]);
            }
            else
            {
                etag = null;
                existing.Remove(FieldId);
                state = serializer.Deserialize<T>(existing);
            }

            grainState.State = state;
            grainState.ETag = etag;
            grainState.RecordExists = true;
        }
        else
        {
            grainState.State = activatorProvider.GetActivator<T>().Create();
            grainState.RecordExists = false;
            grainState.ETag = null;
        }
    }

    public async Task WriteAsync<T>(GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
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

            var result = await GetCollection(cancellationToken).UpdateOneAsync(
                filter,
                Update
                    .Set(FieldEtag, newETag)
                    .Set(FieldDoc, newData),
                new UpdateOptions { IsUpsert = string.IsNullOrEmpty(etag) || etag == "*" }, cancellationToken);

            if (result.MatchedCount == 0 && result.UpsertedId is null)
            {
                await ThrowForOtherEtag(grainKey, etag, cancellationToken);
            }
        }
        catch (MongoException ex) when (ex.IsDuplicateKey())
        {
            await ThrowForOtherEtag(grainKey, etag, cancellationToken, ex);
        }

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearAsync<T>(GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        var grainKey = keyGenerator(grainId);
        var etag = grainState.ETag;
        var filter = Filter.Eq(FieldId, grainKey);
        if (etag != "*")
        {
            filter &= Filter.Eq<BsonValue>(FieldEtag, string.IsNullOrEmpty(etag) ? BsonNull.Value : new BsonString(etag));
        }

        var result = await GetCollection(cancellationToken).DeleteOneAsync(filter, cancellationToken);
        if (result.DeletedCount == 0)
        {
            var existing = await GetCollection(cancellationToken).Find(Filter.Eq(FieldId, grainKey))
                .Project<BsonDocument>(Project.Include(FieldEtag)).FirstOrDefaultAsync(cancellationToken);
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

    private async Task ThrowForOtherEtag(string key, string? etag, CancellationToken cancellationToken, Exception? ex = null)
    {
        var existingEtag =
            await GetCollection(cancellationToken).Find(Filter.Eq(FieldId, key))
                .Project<BsonDocument>(Project.Exclude(FieldDoc)).FirstOrDefaultAsync(cancellationToken);

        var storedEtag = existingEtag?.GetValue(FieldEtag, BsonNull.Value);
        throw new InconsistentStateException(
            "MongoDB grain state ETag mismatch during write.",
            storedEtag is null || storedEtag.IsBsonNull ? null : storedEtag.AsString,
            etag,
            ex);
    }
}
