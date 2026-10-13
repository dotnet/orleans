using System;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;


namespace Orleans.Providers.MongoDB.Utils;

internal abstract class CollectionBase<TEntity>
{
    protected static readonly UpdateOptions Upsert = new UpdateOptions { IsUpsert = true };
    protected static readonly ReplaceOptions UpsertReplace = new ReplaceOptions { IsUpsert = true };
    protected static readonly UpdateDefinitionBuilder<TEntity> Update = Builders<TEntity>.Update;
    protected static readonly FilterDefinitionBuilder<TEntity> Filter = Builders<TEntity>.Filter;
    protected static readonly IndexKeysDefinitionBuilder<TEntity> Index = Builders<TEntity>.IndexKeys;
    protected static readonly ProjectionDefinitionBuilder<TEntity> Project = Builders<TEntity>.Projection;

    private readonly IMongoDatabase mongoDatabase;
    private readonly IMongoClient mongoClient;
    private readonly Action<MongoCollectionSettings>? collectionConfigurator;
    private volatile IMongoCollection<TEntity>? mongoCollection;
    private readonly object mongoCollectionInitializerLock = new();
    private readonly bool createShardKey;

    protected IMongoCollection<TEntity> Collection => GetCollection(CancellationToken.None);

    protected IMongoCollection<TEntity> GetCollection(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mongoCollection == null)
        {
            lock (mongoCollectionInitializerLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mongoCollection ??= CreateCollection(collectionConfigurator, cancellationToken);
            }
        }

        return mongoCollection;
    }

    public IMongoClient Client
    {
        get { return mongoClient; }
    }

    protected CollectionBase(IMongoClient mongoClient, string databaseName,
        Action<MongoCollectionSettings>? collectionConfigurator, bool createShardKey)
    {
        this.mongoClient = mongoClient;
        this.collectionConfigurator = collectionConfigurator;

        mongoDatabase = mongoClient.GetDatabase(databaseName);

        this.createShardKey = createShardKey;
    }

    protected abstract string CollectionName();

    protected virtual void SetupCollection(IMongoCollection<TEntity> collection, CancellationToken cancellationToken)
    {
    }

    private IMongoCollection<TEntity> CreateCollection(Action<MongoCollectionSettings>? collectionConfigurator, CancellationToken cancellationToken)
    {
        var collectionName = CollectionName();

        var collectionSettings = new MongoCollectionSettings();

        collectionConfigurator?.Invoke(collectionSettings);

        var databaseCollection = mongoDatabase.GetCollection<TEntity>(
            collectionName,
            collectionSettings);

        if (createShardKey)
        {
            mongoClient.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument
            {
                ["shardCollection"] = $"{mongoDatabase.DatabaseNamespace.DatabaseName}.{collectionName}",
                ["key"] = new BsonDocument
                {
                    ["_id"] = "hashed"
                }
            }, cancellationToken: cancellationToken);
        }

        SetupCollection(databaseCollection, cancellationToken);

        return databaseCollection;
    }
}
