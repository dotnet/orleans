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

public class GoogleFirestoreGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
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

    public async Task<GrainAddress> Lookup(GrainId grainId)
    {
        try
        {
            var result = await this._dataManager
                .ReadEntity<GrainDirectoryEntity>(Utils.SanitizeGrainId(grainId))
                .ConfigureAwait(false);

            return result is null ? default! : GetGrainAddress(result);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to lookup activation for grain {GrainId} from Firestore", grainId);
            throw;
        }
    }

    public async Task<GrainAddress> Register(GrainAddress address)
    {
        try
        {
            var entry = ConvertToEntity(address);
            await this._dataManager.CreateEntity(entry).ConfigureAwait(false);
            return address;
        }
        catch (RpcException)
        {
            var result = await this.Lookup(address.GrainId);
            return result is null ? default! : result;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to register activation {Activation} for grain {GrainId} in Firestore", address.ActivationId, address.GrainId);
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
            this._logger.LogError(ex, "Unable to unregister activation {Activation} for grain {GrainId} in Firestore", address.ActivationId, address.GrainId);
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
                await this._dataManager.DeleteEntities(entities.ToArray()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to unregister silos | {SiloAddresses} | in Firestore", string.Join('|', siloAddresses));
            throw;
        }
    }

    internal async Task UnregisterMany(List<GrainAddress> addresses)
    {
        try
        {
            const string idField = "__name__";

            var ids = addresses.Select(s => Utils.SanitizeGrainId(s.GrainId)).ToArray();

            var entities = new List<GrainDirectoryEntity>();

            foreach (var chunk in ids.Chunk(MAX_IN_FILTER))
            {
                var fromStorage = await this._dataManager.QueryEntities<GrainDirectoryEntity>(
                    entity => entity
                        .WhereIn(idField, chunk)
                ).ConfigureAwait(false);

                foreach (var entity in fromStorage)
                {
                    var address = addresses.Where(a =>
                        a.GrainId == Utils.ParseGrainId(entity.Id) &&
                        a.ActivationId.ToParsableString() == entity.ActivationId)
                        .SingleOrDefault();

                    if (address is not null)
                    {
                        entities.Add(entity);
                    }
                }
            }

            if (entities.Count > 0)
            {
                await this._dataManager.DeleteEntities(entities.ToArray()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to unregister many activations | {Addresses} | in Firestore", string.Join('|', addresses));
            throw;
        }
    }

    internal static GrainDirectoryEntity ConvertToEntity(GrainAddress address)
    {
        return new GrainDirectoryEntity
        {
            Id = Utils.SanitizeGrainId(address.GrainId),
            SiloAddress = address.SiloAddress!.ToParsableString(),
            ActivationId = address.ActivationId.ToParsableString(),
            MembershipVersion = address.MembershipVersion.Value,
        };
    }

    internal static GrainAddress GetGrainAddress(GrainDirectoryEntity entity)
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

    public async Task Init(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            this._logger.LogInformation("Initializing Google Firestore Grain Directory...");


            await this._dataManager.Initialize();

            this._logger.LogInformation("Initialized Google Firestore Grain Directory in {ElapsedMilliseconds}ms.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error initializing Google Firestore Grain Directory in {ElapsedMilliseconds}.", sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            sw.Stop();
        }
    }
}
