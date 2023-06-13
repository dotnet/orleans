using System;
using System.Text.Json;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;

namespace Orleans.Persistence.GoogleFirestore;

internal class GoogleFirestoreStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string PERSISTENCE_GROUP = "Persistence";

    private readonly FirestoreStateStorageOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _name;
    private FirestoreDataManager _dataManager = default!;

    public GoogleFirestoreStorage(
        string name,
        FirestoreStateStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        ILoggerFactory loggerFactory)
    {
        this._name = name;
        this._options = options;
        this._clusterOptions = clusterOptions.Value;
        this._logger = loggerFactory.CreateLogger<GoogleFirestoreStorage>();
        this._loggerFactory = loggerFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (this._dataManager is null) throw new InvalidOperationException("GoogleFirestoreStorage is not initialized.");

        if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace(
                "Reading: StateName={StateName} GrainId={GrainId} from Firestore",
                stateName,
                grainId);

        var entity = await this._dataManager.ReadEntity<GrainStateEntity>(Utils.SanitizeGrainId(grainId)).ConfigureAwait(false);

        if (entity is null)
        {
            if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace(
                    "Read: GrainId={GrainId} from Firestore returned no data",
                    grainId);
        }
        else
        {
            if (entity.Payload is not null)
            {
                grainState.RecordExists = true;
                grainState.State = JsonSerializer.Deserialize<T>(entity.Payload, this._options.SerializerOptions)!;
            }
            else
            {
                grainState.State = Activator.CreateInstance<T>();
            }
            grainState.ETag = Utils.FormatTimestamp(entity.ETag!.Value);
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (this._dataManager is null) throw new InvalidOperationException("GoogleFirestoreStorage is not initialized.");

        if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace(
            "Reading: StateName={StateName} Grainid={GrainId} ETag = {ETag} from Firestore",
            stateName,
            grainId,
            grainState.ETag);

        var entity = new GrainStateEntity
        {
            Id = Utils.SanitizeGrainId(grainId),
            Name = stateName,
            Payload = JsonSerializer.SerializeToUtf8Bytes(grainState.State, this._options.SerializerOptions)
        };

        try
        {
            string? newETag = null;
            if (grainState.RecordExists)
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
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error writing to GoogleFirestoreStorage GrainId={GrainId} ETag={ETag}", grainId, grainState.ETag);
            throw;
        }
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (this._dataManager is null) throw new InvalidOperationException("GoogleFirestoreStorage is not initialized.");

        if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace(
            "Clearing: StateName={StateName} GrainId={GrainId} ETag={ETag} from Firestore",
            stateName,
            grainId,
            grainState.ETag);

        var operation = "Clearing";

        try
        {
            if (this._options.DeleteStateOnClear)
            {
                operation = "Deleting";
                await this._dataManager.DeleteEntity(Utils.SanitizeGrainId(grainId), grainState.ETag).ConfigureAwait(false);
            }
            else
            {
                var entity = new GrainStateEntity
                {
                    Id = Utils.SanitizeGrainId(grainId),
                    Name = stateName,
                    ETag = Utils.ParseTimestamp(grainState.ETag)
                };

                await this._dataManager.Update(entity).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(
                ex,
                "Error {Operation}: StateName={GrainType} GrainId={GrainId} ETag={ETag} from Firestore",
                operation,
                stateName,
                grainId,
                grainState.ETag);

            throw;
        }
    }

    private async Task Init(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            this._logger.LogInformation("Initializing GoogleFirestoreStorage {ProviderName}...", this._name);
            this._dataManager = new FirestoreDataManager(
                PERSISTENCE_GROUP,
                Utils.SanitizeId(this._clusterOptions.ServiceId),
                this._options,
                this._loggerFactory.CreateLogger<FirestoreDataManager>());

            await this._dataManager.Initialize();

            this._logger.LogInformation("Initializing GoogleFirestoreStorage {ProviderName} in stage took {ElapsedMilliseconds}ms.", this._name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error initializing GoogleFirestoreStorage {ProviderName} in {ElapsedMilliseconds}.", this._name, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            sw.Stop();
        }
    }

    private Task Close(CancellationToken ct) => Task.CompletedTask;

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(OptionFormattingUtilities.Name<GoogleFirestoreStorage>(this._name), ServiceLifecycleStage.ApplicationServices, Init, Close);
}

public static class GoogleFirestoreStorageFactory
{
    public static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsSnapshot = services.GetRequiredService<IOptionsMonitor<FirestoreStateStorageOptions>>();
        var clusterOptions = services.GetProviderClusterOptions(name);
        return ActivatorUtilities.CreateInstance<GoogleFirestoreStorage>(services, name, optionsSnapshot.Get(name), clusterOptions);
    }
}