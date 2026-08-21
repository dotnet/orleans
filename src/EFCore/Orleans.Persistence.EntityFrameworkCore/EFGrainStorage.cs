using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
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
    private readonly IServiceProvider _serviceProvider;

    public EFGrainStorage(
        string name,
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IDbContextFactory<TDbContext> dbContextFactory,
        IEFGrainStorageETagConverter<TETag> eTagConverter,
        IGrainStorageSerializer grainStorageSerializer,
        IServiceProvider serviceProvider)
    {
        this._name = name;
        this._serviceId = clusterOptions.Value.ServiceId;
        this._logger = loggerFactory.CreateLogger<EFGrainStorage<TDbContext, TETag>>();
        this._dbContextFactory = dbContextFactory;
        this._eTagConverter = eTagConverter;
        this._grainStorageSerializer = grainStorageSerializer;
        this._serviceProvider = serviceProvider;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var grainType = grainId.Type.ToString()!;

        var id = grainId.Key.ToString()!;

        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var record = await ctx.GrainState.AsNoTracking().SingleOrDefaultAsync(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainType == grainType &&
                    r.StateType == stateName &&
                    r.GrainId == id)
                .ConfigureAwait(false);

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

        await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var record = new GrainStateRecord<TETag>
        {
            ServiceId = this._serviceId,
            GrainType = grainType,
            StateType = stateName,
            GrainId = id,
            Data = this._grainStorageSerializer.Serialize(grainState.State).ToArray(),
        };

        if (string.IsNullOrWhiteSpace(grainState.ETag))
        {
            ctx.GrainState.Add(record);
        }
        else if (grainState.ETag == ANY_ETAG)
        {
            var etag = await ctx.GrainState.AsNoTracking().Where(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainType == grainType &&
                    r.StateType == stateName &&
                    r.GrainId == id)
                .Select(r => r.ETag)
                .FirstOrDefaultAsync();

            if (etag is not null)
            {
                record.ETag = etag;
            }

            ctx.Update(record);
        }
        else
        {
            record.ETag = this._eTagConverter.ToDbETag(grainState.ETag);
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
            var found = await ctx.GrainState.AsNoTracking().SingleOrDefaultAsync(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainType == grainType &&
                    r.StateType == stateName &&
                    r.GrainId == id)
                .ConfigureAwait(false);
            var foundETag = found is not null ? this._eTagConverter.FromDbETag(found.ETag) : "<null>";

            var isEx = new InconsistentStateException(
                $"Inconsistent state. Operation: Write | State: {stateName} | Grain: {grainType} | GrainId: {id}",
                foundETag, grainState.ETag, ex);

            this._logger.LogError(isEx,
                "Inconsistent state. Operation: {Operation} | State: {State} | Grain: {GrainType} | GrainId: {GrainId} | Expected ETag: {ExpectedETag} | Actual ETag: {ActualETag} ",
                "Write", stateName, grainType, id, grainState.ETag, foundETag);

            throw isEx;
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

        if (!grainState.RecordExists || string.IsNullOrWhiteSpace(grainState.ETag))
        {
            grainState.ETag = null;
            grainState.State = CreateInstance<T>();
            grainState.RecordExists = false;
            return;
        }

        await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            var record = await ctx.GrainState
                .Where(r =>
                    r.ServiceId == this._serviceId &&
                    r.StateType == stateName &&
                    r.GrainType == grainType &&
                    r.GrainId == id)
                .SingleOrDefaultAsync()
                .ConfigureAwait(false);

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
            var found = await ctx.GrainState.AsNoTracking()
                .SingleOrDefaultAsync(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainType == grainType &&
                    r.StateType == stateName &&
                    r.GrainId == id)
                .ConfigureAwait(false);

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

    private T CreateInstance<T>() => ActivatorUtilities.CreateInstance<T>(this._serviceProvider);
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