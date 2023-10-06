using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly IEFGrainStorageETagConverter<TETag> _eTagConverter;

    public EFGrainStorage(
        string name,
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IDbContextFactory<TDbContext> dbContextFactory,
        IEFGrainStorageETagConverter<TETag> eTagConverter,
        IServiceProvider serviceProvider)
    {
        this._serviceProvider = serviceProvider;
        this._name = name;
        this._serviceId = clusterOptions.Value.ServiceId;
        this._logger = loggerFactory.CreateLogger<EFGrainStorage<TDbContext, TETag>>();
        this._dbContextFactory = dbContextFactory;
        this._eTagConverter = eTagConverter;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var grainType = grainId.Type.ToString()!;

        var id = grainId.Key.ToString()!;

        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var record = await ctx.GrainState.AsNoTracking().SingleOrDefaultAsync(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainType == grainType &&
                    r.StateType == stateName &&
                    r.GrainId == id)
                .ConfigureAwait(false);

            if (record is null)
            {
                grainState.State = ActivatorUtilities.CreateInstance<T>(_serviceProvider);
                grainState.RecordExists = false;
                return;
            }

            grainState.State = !string.IsNullOrEmpty(record.Data) ? JsonSerializer.Deserialize<T>(record.Data)! : Activator.CreateInstance<T>();

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

        var ctx = this._dbContextFactory.CreateDbContext();

        var record = new GrainStateRecord<TETag>
        {
            ServiceId = this._serviceId,
            GrainType = grainType,
            StateType = stateName,
            GrainId = id,
            Data = JsonSerializer.Serialize(grainState.State),
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
            var found = await ctx.GrainState.AsNoTracking().SingleOrDefaultAsync(r => r.StateType == grainType && r.GrainId == id).ConfigureAwait(false);
            var foundETag = found is not null ? found.ETag?.ToString() : "<null>";

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
            grainState.State = Activator.CreateInstance<T>();
            return;
        }

        var ctx = this._dbContextFactory.CreateDbContext();

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

            ctx.GrainState.Remove(record);
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var found = await ctx.GrainState.AsNoTracking()
                .SingleOrDefaultAsync(r => r.StateType == stateName && r.GrainId == id).ConfigureAwait(false);

            var foundETag = found is not null ? found.ETag?.ToString() : "<null>";

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
        grainState.State = Activator.CreateInstance<T>();
        grainState.RecordExists = false;
    }

    public void Participate(ISiloLifecycle lifecycle) =>
        this._logger.LogInformation("EFCore Grain Storage {Storage} initialized!", this._name);
}

internal static class EFStorageFactory
{
    public static IGrainStorage Create<TDbContext, TETag>(IServiceProvider services, string name) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        return ActivatorUtilities.CreateInstance<EFGrainStorage<TDbContext, TETag>>(services, name);
    }
}