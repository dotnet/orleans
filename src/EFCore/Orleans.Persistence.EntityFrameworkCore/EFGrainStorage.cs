using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Orleans.Configuration;
using Orleans.Persistence.EntityFrameworkCore.Data;

namespace Orleans.Persistence.EntityFrameworkCore;

internal class EFGrainStorage<TDbContext, TETag> : IGrainStorage, ILifecycleParticipant<ISiloLifecycle> where TDbContext : GrainStateDbContext<TDbContext, TETag>
{
    private const string ANY_ETAG = "*";
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly string _serviceId;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly IEFGrainStorageETagConverter<TETag> _eTagConverter;
    private readonly IGrainStorageSerializer _grainStorageSerializer;
    private readonly IActivatorProvider _activatorProvider;

    public EFGrainStorage(
        string name,
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IDbContextFactory<TDbContext> dbContextFactory,
        IEFGrainStorageETagConverter<TETag> eTagConverter,
        IGrainStorageSerializer grainStorageSerializer,
        IActivatorProvider activatorProvider)
    {
        this._name = name;
        this._serviceId = clusterOptions.Value.ServiceId;
        this._logger = loggerFactory.CreateLogger<EFGrainStorage<TDbContext, TETag>>();
        this._dbContextFactory = dbContextFactory;
        this._eTagConverter = eTagConverter;
        this._grainStorageSerializer = grainStorageSerializer;
        this._activatorProvider = activatorProvider;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var grainType = grainId.Type.ToString()!;

        var id = grainId.Key.ToString()!;
        var keyHash = GetKeyHash(this._serviceId, grainType, stateName, id);

        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var record = await ctx.GrainState.AsNoTracking()
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
                .ConfigureAwait(false);
            VerifyKey(record, this._serviceId, grainType, stateName, id);

            if (record is null)
            {
                grainState.State = CreateInstance<T>();
                grainState.RecordExists = false;
                grainState.ETag = null;
                return;
            }

            grainState.State = record.Data is { Length: > 0 }
                ? this._grainStorageSerializer.Deserialize<T>(record.Data) ?? CreateInstance<T>()
                : CreateInstance<T>();

            grainState.RecordExists = true;
            grainState.ETag = this._eTagConverter.FromDbETag(record.ETag);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex,
                "Unable to read state. State: {State} | Grain: {GrainType} | GrainId: {GrainId}",
                stateName, grainType, id);
            throw;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var grainType = grainId.Type.ToString()!;

        var id = grainId.Key.ToString()!;
        var keyHash = GetKeyHash(this._serviceId, grainType, stateName, id);
        var isInitialWrite = string.IsNullOrWhiteSpace(grainState.ETag);

        await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var record = new GrainStateRecord<TETag>
        {
            KeyHash = keyHash,
            ServiceId = this._serviceId,
            GrainType = grainType,
            StateType = stateName,
            GrainId = id,
            Data = this._grainStorageSerializer.Serialize(grainState.State).ToArray(),
        };

        if (isInitialWrite)
        {
            ctx.GrainState.Add(record);
        }
        else if (grainState.ETag == ANY_ETAG)
        {
            var found = await ctx.GrainState.AsNoTracking()
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
                .ConfigureAwait(false);
            VerifyKey(found, this._serviceId, grainType, stateName, id);

            if (found is not null)
            {
                record.ETag = found.ETag;
            }

            ctx.Update(record);
        }
        else
        {
            record.ETag = this._eTagConverter.ToDbETag(grainState.ETag!);
            ctx.GrainState.Update(record);
        }

        try
        {
            await ctx.SaveChangesAsync().ConfigureAwait(false);
            grainState.ETag = this._eTagConverter.FromDbETag(record.ETag);
            grainState.RecordExists = true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw await CreateInconsistentStateException(
                "Write",
                stateName,
                grainType,
                id,
                keyHash,
                grainState.ETag,
                ex).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (isInitialWrite)
        {
            GrainStateRecord<TETag>? found;
            try
            {
                await using var verification = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                found = await verification.GrainState.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
                    .ConfigureAwait(false);
            }
            catch (Exception verificationException)
            {
                this._logger.LogDebug(
                    verificationException,
                    "Unable to verify whether an initial grain-state write conflict was caused by an existing row");
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            if (found is null)
            {
                throw;
            }

            VerifyKey(found, this._serviceId, grainType, stateName, id);
            var foundETag = this._eTagConverter.FromDbETag(found.ETag);
            var inconsistent = new InconsistentStateException(
                $"Inconsistent state. Operation: Write | State: {stateName} | Grain: {grainType} | GrainId: {id}",
                foundETag,
                grainState.ETag,
                ex);
            this._logger.LogError(
                inconsistent,
                "Inconsistent state. Operation: {Operation} | State: {State} | Grain: {GrainType} | GrainId: {GrainId} | Expected ETag: {ExpectedETag} | Actual ETag: {ActualETag}",
                "Write",
                stateName,
                grainType,
                id,
                grainState.ETag,
                foundETag);
            throw inconsistent;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex,
                "Unable to write grain state. Operation: {Operation} | State: {State} | Grain: {GrainType} | GrainId: {GrainId}",
                "Write", stateName, grainType, id);
            throw;
        }
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var id = grainId.Key.ToString()!;

        var grainType = grainId.Type.ToString()!;
        var keyHash = GetKeyHash(this._serviceId, grainType, stateName, id);

        if (!grainState.RecordExists || string.IsNullOrWhiteSpace(grainState.ETag))
        {
            await using var verification = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var found = await verification.GrainState.AsNoTracking()
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
                .ConfigureAwait(false);
            VerifyKey(found, this._serviceId, grainType, stateName, id);
            grainState.ETag = null;
            grainState.State = CreateInstance<T>();
            grainState.RecordExists = false;
            return;
        }

        await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            var record = await ctx.GrainState
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
                .ConfigureAwait(false);
            VerifyKey(record, this._serviceId, grainType, stateName, id);

            if (record is null)
            {
                throw new DbUpdateConcurrencyException();
            }

            ctx.Entry(record).Property(r => r.ETag).OriginalValue =
                this._eTagConverter.ToDbETag(grainState.ETag);
            ctx.GrainState.Remove(record);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await using var verification = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var found = await verification.GrainState.AsNoTracking()
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
                .ConfigureAwait(false);
            VerifyKey(found, this._serviceId, grainType, stateName, id);

            var foundETag = found is not null ? this._eTagConverter.FromDbETag(found.ETag) : "<null>";

            var isEx = new InconsistentStateException(
                $"Inconsistent state. Operation: Clear | State: {stateName} | GrainType: {grainType} | GrainId: {id}",
                foundETag, grainState.ETag, ex);

            this._logger.LogError(isEx,
                "Inconsistent state. Operation: {Operation} | State: {State} | GrainType: {GrainType} | GrainId: {GrainId} | Expected ETag: {ExpectedETag} | Actual ETag: {ActualETag} ",
                "Clear", stateName, grainType, id, grainState.ETag, foundETag);

            throw isEx;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to write grain state. State: {State} | GrainType: {GrainType} GrainId: {GrainId}", stateName, grainType, grainId);
            throw;
        }

        grainState.ETag = null;
        grainState.State = CreateInstance<T>();
        grainState.RecordExists = false;
    }

    public void Participate(ISiloLifecycle lifecycle) =>
        this._logger.LogInformation("EFCore Grain Storage {Storage} initialized!", this._name);

    private static byte[] GetKeyHash(string serviceId, string grainType, string stateName, string grainId) =>
        EFCoreIdentifierHash.Compute(serviceId, grainType, stateName, grainId);

    private static bool HasKey(
        GrainStateRecord<TETag> record,
        string serviceId,
        string grainType,
        string stateName,
        string grainId) =>
        string.Equals(record.ServiceId, serviceId, StringComparison.Ordinal) &&
        string.Equals(record.GrainType, grainType, StringComparison.Ordinal) &&
        string.Equals(record.StateType, stateName, StringComparison.Ordinal) &&
        string.Equals(record.GrainId, grainId, StringComparison.Ordinal);

    private static void VerifyKey(
        GrainStateRecord<TETag>? record,
        string serviceId,
        string grainType,
        string stateName,
        string grainId)
    {
        if (record is not null && !HasKey(record, serviceId, grainType, stateName, grainId))
        {
            throw new OrleansException(
                $"An Entity Framework Core grain-state identifier hash collision was detected for service '{serviceId}', grain type '{grainType}', state '{stateName}', and grain id '{grainId}'.");
        }
    }

    private async Task<InconsistentStateException> CreateInconsistentStateException(
        string operation,
        string stateName,
        string grainType,
        string grainId,
        byte[] keyHash,
        string? currentETag,
        Exception exception)
    {
        await using var verification = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var found = await verification.GrainState.AsNoTracking()
            .SingleOrDefaultAsync(r => r.KeyHash == keyHash)
            .ConfigureAwait(false);
        VerifyKey(found, this._serviceId, grainType, stateName, grainId);
        var foundETag = found is not null ? this._eTagConverter.FromDbETag(found.ETag) : "<null>";
        var result = new InconsistentStateException(
            $"Inconsistent state. Operation: {operation} | State: {stateName} | Grain: {grainType} | GrainId: {grainId}",
            foundETag,
            currentETag,
            exception);
        this._logger.LogError(
            result,
            "Inconsistent state. Operation: {Operation} | State: {State} | Grain: {GrainType} | GrainId: {GrainId} | Expected ETag: {ExpectedETag} | Actual ETag: {ActualETag}",
            operation,
            stateName,
            grainType,
            grainId,
            currentETag,
            foundETag);
        return result;
    }

    private T CreateInstance<T>() => this._activatorProvider.GetActivator<T>().Create();
}

internal static class EFStorageFactory
{
    public static EFGrainStorage<TDbContext, TETag> Create<TDbContext, TETag>(IServiceProvider services, string name) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        var dbContextFactory = services.GetKeyedService<IDbContextFactory<TDbContext>>(name)
            ?? services.GetRequiredService<IDbContextFactory<TDbContext>>();
        var grainStorageSerializer = services.GetKeyedService<IGrainStorageSerializer>(name)
            ?? services.GetRequiredService<IGrainStorageSerializer>();

        return ActivatorUtilities.CreateInstance<EFGrainStorage<TDbContext, TETag>>(
            services,
            name,
            dbContextFactory,
            grainStorageSerializer);
    }
}