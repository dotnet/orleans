using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace Orleans.Providers.MongoDB.StorageProviders;

/// <summary>
/// Persists Orleans grain state in MongoDB using ETags for optimistic concurrency.
/// </summary>
public class MongoGrainStorage : IGrainStorage
{
    private readonly ConcurrentDictionary<string, MongoGrainStorageCollection> collections = new ConcurrentDictionary<string, MongoGrainStorageCollection>();
    private readonly MongoDBGrainStorageOptions options;
    private readonly IMongoClient mongoClient;
    private readonly ILogger<MongoGrainStorage> logger;
    private readonly IGrainStateSerializer serializer;
    private readonly IActivatorProvider activatorProvider;

    /// <summary>
    /// Initializes storage with validated and post-configured provider options.
    /// </summary>
    /// <param name="mongoClient">The reusable MongoDB client owned by the application or its service container.</param>
    /// <param name="logger">The provider logger.</param>
    /// <param name="options">The validated storage options and configured serializer.</param>
    /// <param name="activatorProvider">The Orleans activators used to recreate missing or cleared grain state.</param>
    public MongoGrainStorage(
        IMongoClient mongoClient,
        ILogger<MongoGrainStorage> logger,
        MongoDBGrainStorageOptions options,
        IActivatorProvider activatorProvider)
    {
        this.mongoClient = mongoClient;
        this.logger = logger;
        this.options = options;
        this.serializer = options.GrainStateSerializer;
        this.activatorProvider = activatorProvider;
    }

    /// <inheritdoc />
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => ReadStateAsync(stateName, grainId, grainState, CancellationToken.None);

    /// <inheritdoc />
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(ReadStateAsync), () =>
        {
            return GetCollection<T>(stateName, grainId).ReadAsync(grainId, grainState, cancellationToken);
        });
    }
    /// <inheritdoc />
    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => WriteStateAsync(stateName, grainId, grainState, CancellationToken.None);

    /// <inheritdoc />
    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(WriteStateAsync), () =>
        {
            return GetCollection<T>(stateName, grainId).WriteAsync(grainId, grainState, cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => ClearStateAsync(stateName, grainId, grainState, CancellationToken.None);

    /// <inheritdoc />
    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoAndLog(nameof(ClearStateAsync), () =>
        {
            return GetCollection<T>(stateName, grainId).ClearAsync(grainId, grainState, cancellationToken);
        });
    }

    private MongoGrainStorageCollection GetCollection<T>(string stateName, GrainId grainId)
    {
        var collectionName = $"{options.CollectionPrefix}{ReturnGrainName<T>(stateName, grainId)}";

        return collections.GetOrAdd(collectionName, x =>
            new MongoGrainStorageCollection(
                mongoClient,
                options.DatabaseName,
                collectionName,
                options.CollectionConfigurator,
                options.CreateShardKeyForCosmos,
                serializer,
                activatorProvider,
                options.KeyGenerator));
    }

    private Task DoAndLog(string actionName, Func<Task> action)
    {
        return DoAndLog(actionName, async () => { await action(); return true; });
    }

    private async Task<T> DoAndLog<T>(string actionName, Func<Task<T>> action)
    {
        logger.LogDebug($"{nameof(MongoGrainStorage)}.{actionName} called.");

        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogError((int)MongoProviderErrorCode.GrainStorageOperations, ex, $"{nameof(MongoGrainStorage)}.{actionName} failed. Exception={ex.Message}");

            throw;
        }
    }

    /// <summary>
    /// Gets the collection suffix for a grain state, preserving the contributed provider's naming format.
    /// </summary>
    /// <typeparam name="T">The persisted state type.</typeparam>
    /// <param name="stateName">The configured state name.</param>
    /// <param name="grainId">The grain identity.</param>
    /// <returns>The state name, or the state type name when the state name is <c>state</c>.</returns>
    protected virtual string ReturnGrainName<T>(string stateName, GrainId grainId)
    {
        return stateName != "state" ? stateName : typeof(T).Name;
    }
}
