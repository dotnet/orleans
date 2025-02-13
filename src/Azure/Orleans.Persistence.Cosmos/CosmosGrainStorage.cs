// <copyright file="CosmosGrainStorage.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Persistence.Cosmos.TypeInfo;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Grain storage provider which uses Azure Cosmos DB as the backing store.
/// </summary>
internal sealed class CosmosGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string KEY_STRING_SEPARATOR = "__";

    // for access from GrainStateTypeInfoProvider implementations
    internal static readonly MethodInfo ReadStateAsyncCoreMethodInfo = typeof(CosmosGrainStorage).GetMethod(nameof(ReadStateAsyncCore), 1, BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(string), typeof(GrainId), typeof(IGrainState) }, null)!;
    internal static readonly MethodInfo WriteStateAsyncCoreMethodInfo = typeof(CosmosGrainStorage).GetMethod(nameof(WriteStateAsyncCore), 1, BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(string), typeof(GrainId), typeof(IGrainState) }, null)!;
    internal static readonly MethodInfo ClearStateAsyncCoreMethodInfo = typeof(CosmosGrainStorage).GetMethod(nameof(ClearStateAsyncCore), 1, BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(string), typeof(GrainId), typeof(IGrainState) }, null)!;

    private readonly IGrainStateTypeInfoProvider grainStateTypeInfoProvider;

    private readonly IDocumentIdProvider idProvider;
    private readonly CosmosGrainStorageOptions options;
    private readonly string serviceId;
    private readonly string name;
    private readonly IServiceProvider serviceProvider;

    private CosmosClient client = default!;
    private Container container = default!;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosGrainStorage"/> class.
    /// </summary>
    /// <param name="name">The provider name.</param>
    /// <param name="options">The options.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="idProvider">The partition key provider.</param>
    /// <param name="clusterOptions">Cluster options</param>
    /// <param name="grainStateTypeInfoProvider">Provides grain state type info via activation context</param>
    public CosmosGrainStorage(
        string name,
        CosmosGrainStorageOptions options,
        IServiceProvider serviceProvider,
        IDocumentIdProvider idProvider,
        IOptions<ClusterOptions> clusterOptions,
        IGrainStateTypeInfoProvider grainStateTypeInfoProvider)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(idProvider);
        ArgumentNullException.ThrowIfNull(grainStateTypeInfoProvider);

        this.name = name;
        this.options = options;
        this.serviceProvider = serviceProvider;
        this.idProvider = idProvider;
        this.serviceId = clusterOptions.Value.ServiceId;
        this.grainStateTypeInfoProvider = grainStateTypeInfoProvider;
    }

    /// <inheritdoc/>
    public Task ReadStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
    {
        var grainTypeData = grainStateTypeInfoProvider.GetGrainStateTypeInfo(this, grainReference, grainState);
        return grainTypeData.ReadStateAsync(stateName, grainReference, grainState);
    }

    /// <inheritdoc/>
    public Task WriteStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
    {
        var grainTypeData = grainStateTypeInfoProvider.GetGrainStateTypeInfo(this, grainReference, grainState);
        return grainTypeData.WriteStateAsync(stateName, grainReference, grainState);
    }

    /// <inheritdoc/>
    public Task ClearStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
    {
        var grainTypeData = grainStateTypeInfoProvider.GetGrainStateTypeInfo(this, grainReference, grainState);
        return grainTypeData.ClearStateAsync(stateName, grainReference, grainState);
    }

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(OptionFormattingUtilities.Name<CosmosGrainStorage>(this.name), this.options.InitStage, this.Init);
    }

    private static int? GetTimeToLive(IGrainState grainState)
    {
        if (grainState.State is ITimeToLiveAware ttlAware)
        {
            int? ttl = ttlAware.GetTimeToLive();

            if (ttl.HasValue && (ttl.Value < -1 || ttl.Value == 0))
            {
                throw new InvalidOperationException($"The grain state TTL must be -1 or a positive integer. Current value is '{ttl.Value}' for the '{grainState.Type.FullName}' type.");
            }

            return ttl;
        }

        return null;
    }

    /// <summary>
    /// Adds extra information to the <paramref name="destination"/> exception
    /// extracted from the <paramref name="source"/> exception.
    /// </summary>
    /// <param name="destination">
    /// The exception to enreach with additional information.
    /// </param>
    /// <param name="source">
    /// The exception from which to take additional information.
    /// </param>
    /// <param name="grainId">
    /// The ID of the grain for which the exception happened.
    /// </param>
    /// <returns>
    /// The <paramref name="destination"/> exception that was passed in for chaining.
    /// </returns>
    private static Exception EnreachException(Exception destination, Exception source, GrainId grainId)
    {
        var data = destination.Data;
        var originalStackTrace = source.StackTrace;

        if (!string.IsNullOrEmpty(originalStackTrace))
        {
            ExceptionDispatchInfo.SetRemoteStackTrace(destination, originalStackTrace);
        }

        data["InnerType"] = source.GetType().FullName;
        data["GrainId"] = grainId.ToString();

        if (source is CosmosException cosmosEx)
        {
            data["StatusCode"] = (int)cosmosEx.StatusCode;

            if (cosmosEx.SubStatusCode != 0)
            {
                data["SubStatusCode"] = cosmosEx.SubStatusCode;
            }
        }

        return destination;
    }

    private static Exception CreateOrleansException(Exception source, GrainId grainId)
    {
        return EnreachException(new OrleansException(source.Message), source, grainId);
    }

    private static Exception CreateInconsistentStateException(CosmosException source, IGrainState grainState, GrainId grainId)
    {
        return EnreachException(
                new InconsistentStateException(
                    source.Message,
                    storedEtag: source.Headers.ETag,
                    currentEtag: grainState.ETag),
                source,
                grainId);
    }

    private async Task ReadStateAsyncCore<T>(string stateName, GrainId grainId, IGrainState grainState)
    {
        try
        {
            var (documentId, partitionKey) = this.idProvider.GetDocumentIdentifiers(stateName, grainId.Type, grainId.Key);

            if (options.UseLegacyFormat)
            {
                var response = await this.container.ReadItemAsync<LegacyGrainStateEntity<T>>(documentId, new PartitionKey(partitionKey)).ConfigureAwait(false);

                grainState.State = response.Resource.State;
                grainState.ETag = response.ETag;
                grainState.RecordExists = true;

                if (grainState.State is ITimeToLiveAware grainStateTtlAware)
                {
                    grainStateTtlAware.SetTimeToLive(response.Resource.Ttl);
                }
            }
            else
            {
                var response = await this.container.ReadItemAsync<GrainStateEntity<T>>(documentId, new PartitionKey(partitionKey)).ConfigureAwait(false);

                grainState.State = response.Resource.State;
                grainState.ETag = response.ETag;
                grainState.RecordExists = true;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // State is new, just activate a default and return.
            grainState.State = ActivatorUtilities.CreateInstance(this.serviceProvider, grainState.Type);

            grainState.ETag = null;
            grainState.RecordExists = false;
        }
        catch (Exception ex)
        {
            throw CreateOrleansException(ex, grainId);
        }
    }

    private async Task WriteStateAsyncCore<T>(string stateName, GrainId grainId, IGrainState grainState)
    {
        try
        {
            var (documentId, partitionKey) = this.idProvider.GetDocumentIdentifiers(stateName, grainId.Type, grainId.Key);

            if (options.UseLegacyFormat)
            {
                var entity = new LegacyGrainStateEntity<T>
                {
                    ETag = grainState.ETag,
                    Id = GetId(documentId),
                    Type = grainId.Type,
                    State = (T)grainState.State,
                    PartitionKey = partitionKey,
                    Ttl = GetTimeToLive(grainState),
                };

                var pk = new PartitionKey(partitionKey);

                Task<ItemResponse<LegacyGrainStateEntity<T>>> responseTask;

                if (string.IsNullOrEmpty(grainState.ETag))
                {
                    responseTask = this.container.CreateItemAsync(entity, pk);
                }
                else if (grainState.ETag == "*") // AnyETag
                {
                    responseTask = this.container.UpsertItemAsync(entity, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag });
                }
                else
                {
                    responseTask = this.container.ReplaceItemAsync(entity, entity.Id, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag });
                }

                var response = await responseTask.ConfigureAwait(false);

                grainState.ETag = response.ETag;
                grainState.RecordExists = true;
            }
            else
            {
                var entity = new GrainStateEntity<T>
                {
                    ETag = grainState.ETag,
                    Id = GetId(documentId),
                    GrainType = stateName,
                    State = (T)grainState.State,
                    PartitionKey = partitionKey,
                };

                var pk = new PartitionKey(partitionKey);

                Task<ItemResponse<GrainStateEntity<T>>> responseTask;

                if (string.IsNullOrEmpty(grainState.ETag))
                {
                    responseTask = this.container.CreateItemAsync(entity, pk);
                }
                else if (grainState.ETag == "*") // AnyETag
                {
                    responseTask = this.container.UpsertItemAsync(entity, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag });
                }
                else
                {
                    responseTask = this.container.ReplaceItemAsync(entity, entity.Id, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag });
                }

                var response = await responseTask.ConfigureAwait(false);

                grainState.ETag = response.ETag;
                grainState.RecordExists = true;
            }
        }
        catch (CosmosException ex) when (
            ex.StatusCode == HttpStatusCode.PreconditionFailed ||
            ex.StatusCode == HttpStatusCode.Conflict ||
            ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw CreateInconsistentStateException(ex, grainState, grainId);
        }
        catch (Exception ex)
        {
            throw CreateOrleansException(ex, grainId);
        }
    }

    private async Task ClearStateAsyncCore<T>(string stateName, GrainId grainId, IGrainState grainState)
    {
        try
        {
            var (documentId, partitionKey) = this.idProvider.GetDocumentIdentifiers(stateName, grainId.Type, grainId.Key);

            var pk = new PartitionKey(partitionKey);

            var defaultState = ActivatorUtilities.CreateInstance(this.serviceProvider, grainState.Type);

            if (this.options.DeleteStateOnClear)
            {
                if (!string.IsNullOrEmpty(grainState.ETag))
                {
                    if (options.UseLegacyFormat)
                    {
                        await this.container.DeleteItemAsync<LegacyGrainStateEntity<T>>(documentId, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag }).ConfigureAwait(false);
                    }
                    else
                    {
                        await this.container.DeleteItemAsync<GrainStateEntity<T>>(documentId, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag }).ConfigureAwait(false);
                    }
                }

                grainState.ETag = null;
                grainState.RecordExists = false;
            }
            else
            {
                if (options.UseLegacyFormat)
                {
                    var entity = new LegacyGrainStateEntity<T>
                    {
                        ETag = grainState.ETag,
                        Id = GetId(documentId),
                        Type = grainId.Type,
                        State = (T)defaultState,
                        PartitionKey = partitionKey,
                        Ttl = GetTimeToLive(grainState),
                    };

                    var responseTask = string.IsNullOrEmpty(grainState.ETag) ?
                        this.container.CreateItemAsync(entity, pk) :
                        this.container.ReplaceItemAsync(entity, entity.Id, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag }); // AnyETag or item etag

                    var response = await responseTask.ConfigureAwait(false);

                    grainState.ETag = response.ETag;
                    grainState.RecordExists = true;
                }
                else
                {
                    var entity = new GrainStateEntity<T>
                    {
                        ETag = grainState.ETag,
                        Id = GetId(documentId),
                        GrainType = stateName,
                        State = (T)defaultState,
                        PartitionKey = partitionKey,
                    };

                    var responseTask = string.IsNullOrEmpty(grainState.ETag) ?
                        this.container.CreateItemAsync(entity, pk) :
                        this.container.ReplaceItemAsync(entity, entity.Id, pk, new ItemRequestOptions { IfMatchEtag = grainState.ETag }); // AnyETag or item etag

                    var response = await responseTask.ConfigureAwait(false);

                    grainState.ETag = response.ETag;
                    grainState.RecordExists = true;
                }
            }

            // Set the state to default only on successful storage write operation.
            grainState.State = defaultState;
        }
        catch (CosmosException ex) when (
            ex.StatusCode == HttpStatusCode.PreconditionFailed ||
            ex.StatusCode == HttpStatusCode.Conflict ||
            ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw CreateInconsistentStateException(ex, grainState, grainId);
        }
        catch (Exception ex)
        {
            throw CreateOrleansException(ex, grainId);
        }
    }

    private async Task Init(CancellationToken ct)
    {
        try
        {
            this.client = await this.options.CreateClient(this.serviceProvider).ConfigureAwait(false);

            this.container = this.client.GetContainer(this.options.DatabaseName, this.options.ContainerName);
        }
        catch (Exception ex)
        {
            throw CreateOrleansException(ex, default);
        }
    }

    internal string GetId(string documentId)
    {
        if (options.UseLegacyFormat)
        {
            return documentId;
        }

        return $"{CosmosIdSanitizer.Sanitize(serviceId)}{KEY_STRING_SEPARATOR}{documentId}";
    }
}
