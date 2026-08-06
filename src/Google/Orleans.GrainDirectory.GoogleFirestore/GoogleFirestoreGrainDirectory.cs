using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Grpc.Core;
using Orleans.Runtime;
using Orleans.Configuration;

namespace Orleans.GrainDirectory.GoogleFirestore;

public partial class GoogleFirestoreGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
{
    private const int MAX_IN_FILTER = 10;
    private const string DIRECTORY_GROUP = "GrainDirectory";
    private readonly string _clusterId;
    private readonly ILogger _logger;
    private readonly FirestoreDataManager _dataManager;

    public GoogleFirestoreGrainDirectory(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<FirestoreOptions> firestoreOptions,
        ILoggerFactory loggerFactory)
    {
        this._clusterId = clusterOptions.Value.ClusterId;
        this._logger = loggerFactory.CreateLogger<GoogleFirestoreGrainDirectory>();

        this._dataManager = new FirestoreDataManager(
            DIRECTORY_GROUP,
            Utils.SanitizeId(this._clusterId),
            firestoreOptions.Value,
            loggerFactory.CreateLogger<FirestoreDataManager>());
    }

    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        try
        {
            var result = await this._dataManager
                .ReadEntity<GrainDirectoryEntity>(Utils.SanitizeGrainId(grainId))
                .ConfigureAwait(false);

            return result is null ? null : GetGrainAddress(result);
        }
        catch (Exception ex)
        {
            LogLookupError(ex, grainId);
            throw;
        }
    }

    public async Task<GrainAddress?> Register(GrainAddress address)
    {
        try
        {
            var entry = ConvertToEntity(address);
            await this._dataManager.CreateEntity(entry).ConfigureAwait(false);
            return address;
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            var result = await this.Lookup(address.GrainId);
            return result;
        }
        catch (Exception ex)
        {
            LogRegisterError(ex, address.ActivationId, address.GrainId);
            throw;
        }
    }

    public async Task Unregister(GrainAddress address)
    {
        try
        {
            var found = await this._dataManager.ReadEntity<GrainDirectoryEntity>(Utils.SanitizeGrainId(address.GrainId)).ConfigureAwait(false);

            if (found is null) return;

            if (found.ActivationId == address.ActivationId.ToParsableString())
            {
                await this._dataManager.DeleteEntity(found.Id, Utils.FormatTimestamp(found.ETag!.Value)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogUnregisterError(ex, address.ActivationId, address.GrainId);
            throw;
        }
    }

    public async Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        try
        {
            var entities = new List<GrainDirectoryEntity>();

            var silos = siloAddresses.Select(s => s.ToParsableString()).ToArray();

            foreach (var chunk in silos.Chunk(MAX_IN_FILTER))
            {
                var found = await this._dataManager.QueryEntities<GrainDirectoryEntity>(
                    entity => entity
                        .WhereIn(nameof(GrainDirectoryEntity.SiloAddress), chunk)
                ).ConfigureAwait(false);

                entities.AddRange(found);
            }

            if (entities.Count > 0)
            {
                foreach (var chunk in entities.Chunk(FirestoreDataManager.MAX_BATCH_ENTRIES))
                {
                    await this._dataManager.DeleteEntities(chunk).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogUnregisterSilosError(ex, string.Join('|', siloAddresses));
            throw;
        }
    }

    private static GrainDirectoryEntity ConvertToEntity(GrainAddress address)
    {
        return new GrainDirectoryEntity
        {
            Id = Utils.SanitizeGrainId(address.GrainId),
            SiloAddress = address.SiloAddress!.ToParsableString(),
            ActivationId = address.ActivationId.ToParsableString(),
            MembershipVersion = address.MembershipVersion.Value,
        };
    }

    private static GrainAddress GetGrainAddress(GrainDirectoryEntity entity)
    {
        return new GrainAddress
        {
            GrainId = Utils.ParseGrainId(entity.Id),
            SiloAddress = SiloAddress.FromParsableString(entity.SiloAddress),
            ActivationId = ActivationId.FromParsableString(entity.ActivationId),
            MembershipVersion = new MembershipVersion(entity.MembershipVersion)
        };
    }

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(GoogleFirestoreGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, Init);

    private async Task Init(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            LogInitializing();


            await this._dataManager.Initialize();

            LogInitialized(sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            LogInitializationError(ex, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            sw.Stop();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unable to lookup activation for grain {GrainId} from Firestore")]
    private partial void LogLookupError(Exception exception, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unable to register activation {Activation} for grain {GrainId} in Firestore")]
    private partial void LogRegisterError(Exception exception, ActivationId activation, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unable to unregister activation {Activation} for grain {GrainId} in Firestore")]
    private partial void LogUnregisterError(Exception exception, ActivationId activation, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unable to unregister silos | {SiloAddresses} | in Firestore")]
    private partial void LogUnregisterSilosError(Exception exception, string siloAddresses);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initializing Google Firestore Grain Directory...")]
    private partial void LogInitializing();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initialized Google Firestore Grain Directory in {ElapsedMilliseconds}ms.")]
    private partial void LogInitialized(long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error initializing Google Firestore Grain Directory in {ElapsedMilliseconds}ms.")]
    private partial void LogInitializationError(Exception exception, long elapsedMilliseconds);
}
