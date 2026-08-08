using System;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Serialization.Serializers;

namespace Orleans.Persistence.Firestore;

internal partial class FirestoreGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string PERSISTENCE_GROUP = "Persistence";

    private readonly FirestoreStateStorageOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _name;
    private readonly IActivatorProvider _activatorProvider;
    private readonly IGrainStorageSerializer _grainStorageSerializer;
    private FirestoreDataManager _dataManager = default!;

    public FirestoreGrainStorage(
        string name,
        FirestoreStateStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        IActivatorProvider activatorProvider,
        ILoggerFactory loggerFactory)
    {
        this._name = name;
        this._options = options;
        this._clusterOptions = clusterOptions.Value;
        this._activatorProvider = activatorProvider;
        this._grainStorageSerializer = options.GrainStorageSerializer;
        this._logger = loggerFactory.CreateLogger<FirestoreGrainStorage>();
        this._loggerFactory = loggerFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (this._dataManager is null) throw new InvalidOperationException("FirestoreGrainStorage is not initialized.");

        LogReadingState(stateName, grainId);

        var entity = await this._dataManager.ReadEntity<GrainStateEntity>(GetDocumentId(stateName, grainId)).ConfigureAwait(false);

        if (entity?.Payload is not { Length: > 0 })
        {
            LogReadReturnedNoData(grainId);
            ResetGrainState(grainState);
            if (entity?.ETag is { } etag)
            {
                grainState.ETag = Utils.FormatTimestamp(etag);
            }
        }
        else
        {
            var loadedState = this._grainStorageSerializer.Deserialize<T>(entity.Payload);
            grainState.RecordExists = loadedState is not null;
            grainState.State = loadedState ?? CreateInstance<T>();
            grainState.ETag = Utils.FormatTimestamp(entity.ETag!.Value);
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (this._dataManager is null) throw new InvalidOperationException("FirestoreGrainStorage is not initialized.");

        LogWritingState(stateName, grainId, grainState.ETag);

        var entity = new GrainStateEntity
        {
            Id = GetDocumentId(stateName, grainId),
            Name = stateName,
            Payload = this._grainStorageSerializer.Serialize(grainState.State).ToArray()
        };

        try
        {
            string newETag;
            if (grainState.ETag == "*")
            {
                newETag = await this._dataManager.UpdateUnconditionally(entity).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(grainState.ETag))
            {
                entity.ETag = Utils.ParseTimestamp(grainState.ETag);
                newETag = await this._dataManager.Update(entity).ConfigureAwait(false);
            }
            else
            {
                newETag = await this._dataManager.CreateEntity(entity).ConfigureAwait(false);
            }
            
            grainState.ETag = newETag;
            grainState.RecordExists = true;
        }
        catch (RpcException ex) when (IsConcurrencyFailure(ex))
        {
            throw CreateInconsistentStateException(nameof(WriteStateAsync), stateName, grainId, grainState.ETag, ex);
        }
        catch (FormatException ex)
        {
            throw CreateInconsistentStateException(nameof(WriteStateAsync), stateName, grainId, grainState.ETag, ex);
        }
        catch (Exception ex)
        {
            LogWriteError(ex, grainId, grainState.ETag);
            throw;
        }
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (this._dataManager is null) throw new InvalidOperationException("FirestoreGrainStorage is not initialized.");

        LogClearingState(stateName, grainId, grainState.ETag);

        var operation = "Clearing";

        try
        {
            if (this._options.DeleteStateOnClear)
            {
                operation = "Deleting";
                var documentId = GetDocumentId(stateName, grainId);
                if (string.IsNullOrWhiteSpace(grainState.ETag))
                {
                    if (await this._dataManager.EntityExists(documentId).ConfigureAwait(false))
                    {
                        throw CreateInconsistentStateException(nameof(ClearStateAsync), stateName, grainId, grainState.ETag);
                    }
                }
                else if (!await this._dataManager.DeleteEntity(documentId, grainState.ETag).ConfigureAwait(false))
                {
                    throw CreateInconsistentStateException(nameof(ClearStateAsync), stateName, grainId, grainState.ETag);
                }

                grainState.ETag = null;
            }
            else
            {
                var entity = new GrainStateEntity
                {
                    Id = GetDocumentId(stateName, grainId),
                    Name = stateName,
                };

                grainState.ETag = grainState.ETag switch
                {
                    "*" => await this._dataManager.UpdateUnconditionally(entity).ConfigureAwait(false),
                    { Length: > 0 } etag => await UpdateClearedEntity(entity, etag).ConfigureAwait(false),
                    _ => await this._dataManager.CreateEntity(entity).ConfigureAwait(false),
                };
            }

            grainState.RecordExists = false;
            grainState.State = CreateInstance<T>();
        }
        catch (RpcException ex) when (IsConcurrencyFailure(ex))
        {
            throw CreateInconsistentStateException(nameof(ClearStateAsync), stateName, grainId, grainState.ETag, ex);
        }
        catch (FormatException ex)
        {
            throw CreateInconsistentStateException(nameof(ClearStateAsync), stateName, grainId, grainState.ETag, ex);
        }
        catch (Exception ex)
        {
            LogClearError(ex, operation, stateName, grainId, grainState.ETag);
            throw;
        }
    }

    private async Task<string> UpdateClearedEntity(GrainStateEntity entity, string etag)
    {
        entity.ETag = Utils.ParseTimestamp(etag);
        return await this._dataManager.Update(entity).ConfigureAwait(false);
    }

    private static string GetDocumentId(string stateName, GrainId grainId) =>
        Utils.SanitizeId($"{stateName}\0{grainId}");

    private void ResetGrainState<T>(IGrainState<T> grainState)
    {
        grainState.ETag = null;
        grainState.RecordExists = false;
        grainState.State = CreateInstance<T>();
    }

    private T CreateInstance<T>() => this._activatorProvider.GetActivator<T>().Create();

    private static bool IsConcurrencyFailure(RpcException exception) =>
        exception.StatusCode is StatusCode.Aborted or StatusCode.AlreadyExists or StatusCode.FailedPrecondition or StatusCode.NotFound;

    private InconsistentStateException CreateInconsistentStateException(
        string operation,
        string stateName,
        GrainId grainId,
        string? etag,
        Exception? exception = null)
    {
        var message = $"Version conflict ({operation}): ServiceId={this._clusterOptions.ServiceId} ProviderName={this._name} StateName={stateName} GrainId={grainId} ETag={etag}.";
        return exception is null
            ? new InconsistentStateException(message, "Unknown", etag)
            : new InconsistentStateException(message, "Unknown", etag, exception);
    }

    private async Task Init(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            LogInitializing(this._name);
            this._dataManager = new FirestoreDataManager(
                PERSISTENCE_GROUP,
                Utils.SanitizeId(this._clusterOptions.ServiceId),
                this._options,
                this._loggerFactory.CreateLogger<FirestoreDataManager>());

            await this._dataManager.Initialize(ct);

            LogInitialized(this._name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInitializationError(ex, this._name, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            sw.Stop();
        }
    }

    private Task Close(CancellationToken ct) => Task.CompletedTask;

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(OptionFormattingUtilities.Name<FirestoreGrainStorage>(this._name), ServiceLifecycleStage.ApplicationServices, Init, Close);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Reading: StateName={StateName} GrainId={GrainId} from Firestore")]
    private partial void LogReadingState(string stateName, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Read: GrainId={GrainId} from Firestore returned no data")]
    private partial void LogReadReturnedNoData(GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Writing: StateName={StateName} GrainId={GrainId} ETag={ETag} to Firestore")]
    private partial void LogWritingState(string stateName, GrainId grainId, string? etag);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error writing to FirestoreGrainStorage GrainId={GrainId} ETag={ETag}")]
    private partial void LogWriteError(Exception exception, GrainId grainId, string? etag);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Clearing: StateName={StateName} GrainId={GrainId} ETag={ETag} from Firestore")]
    private partial void LogClearingState(string stateName, GrainId grainId, string? etag);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error {Operation}: StateName={StateName} GrainId={GrainId} ETag={ETag} from Firestore")]
    private partial void LogClearError(Exception exception, string operation, string stateName, GrainId grainId, string? etag);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initializing FirestoreGrainStorage {ProviderName}...")]
    private partial void LogInitializing(string providerName);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initialized FirestoreGrainStorage {ProviderName} in {ElapsedMilliseconds}ms.")]
    private partial void LogInitialized(string providerName, long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error initializing FirestoreGrainStorage {ProviderName} in {ElapsedMilliseconds}ms.")]
    private partial void LogInitializationError(Exception exception, string providerName, long elapsedMilliseconds);
}

internal static class FirestoreGrainStorageFactory
{
    public static FirestoreGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsSnapshot = services.GetRequiredService<IOptionsMonitor<FirestoreStateStorageOptions>>();
        var clusterOptions = services.GetProviderClusterOptions(name);
        return ActivatorUtilities.CreateInstance<FirestoreGrainStorage>(services, name, optionsSnapshot.Get(name), clusterOptions);
    }
}