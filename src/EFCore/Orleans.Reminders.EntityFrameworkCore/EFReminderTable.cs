using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore;

public class EFReminderTable<TDbContext, TETag> : IReminderTable where TDbContext : ReminderDbContext<TDbContext, TETag>
{
    private readonly ILogger _logger;
    private readonly string _serviceId;
    private readonly byte[] _serviceIdHash;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly IEFReminderETagConverter<TETag> _eTagConverter;

    public EFReminderTable(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IDbContextFactory<TDbContext> dbContextFactory,
        IEFReminderETagConverter<TETag> eTagConverter)
    {
        this._logger = loggerFactory.CreateLogger<EFReminderTable<TDbContext, TETag>>();
        this._serviceId = clusterOptions.Value.ServiceId;
        this._serviceIdHash = EFCoreIdentifierHash.Compute(this._serviceId);
        this._dbContextFactory = dbContextFactory;
        this._eTagConverter = eTagConverter;
    }

    public Task Init()
    {
        if (this._logger.IsEnabled(LogLevel.Debug))
        {
            this._logger.LogDebug("EFCore Reminder table initialized!");
        }

        return Task.CompletedTask;
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var grainIdValue = grainId.ToString();
            var grainIdHash = EFCoreIdentifierHash.Compute(grainIdValue);
            var candidates = await ctx.Reminders.AsNoTracking().Where(r =>
                    r.ServiceIdHash == this._serviceIdHash &&
                    r.GrainIdHash == grainIdHash)
                .ToArrayAsync().ConfigureAwait(false);
            EnsureExactGrain(candidates, this._serviceId, grainIdValue);

            return new ReminderTableData(candidates.Select(ConvertToEntity));
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Failure reading reminders for grain {GrainId}", grainId);
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var query = ctx.Reminders.AsNoTracking()
                .Where(r => r.ServiceIdHash == this._serviceIdHash);

            query = begin < end
                ? query.Where(r => r.GrainHash > begin && r.GrainHash <= end)
                : query.Where(r => r.GrainHash > begin || r.GrainHash <= end);

            var records = await query.ToArrayAsync().ConfigureAwait(false);
            EnsureExactService(records, this._serviceId);

            return new ReminderTableData(records.Select(ConvertToEntity));
        }
        catch (Exception exc)
        {
            this._logger.LogError(
                exc,
                "Failure reading reminders for service {Service} for range {Begin} to {End}",
                this._serviceId,
                begin.ToString("X"),
                end.ToString("X"));
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var grainIdValue = grainId.ToString();
            var grainIdHash = EFCoreIdentifierHash.Compute(grainIdValue);
            var reminderNameHash = EFCoreIdentifierHash.Compute(reminderName);
            var candidates = await ctx.Reminders
                .AsNoTracking()
                .Where(r =>
                    r.ServiceIdHash == this._serviceIdHash &&
                    r.GrainIdHash == grainIdHash &&
                    r.ReminderNameHash == reminderNameHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var record = GetExactRecord(candidates, this._serviceId, grainIdValue, reminderName);

            return record is null ? null : ConvertToEntity(record);
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure reading reminder {Name} for service {ServiceId} and grain {GrainId}", reminderName, this._serviceId, grainId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<string?> UpsertRow(ReminderEntry entry)
    {
        try
        {
            var record = ConvertToRecord(entry);

            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var candidates = await ctx.Reminders
                .AsNoTracking()
                .Where(r =>
                    r.ServiceIdHash == record.ServiceIdHash &&
                    r.GrainIdHash == record.GrainIdHash &&
                    r.ReminderNameHash == record.ReminderNameHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var foundRecord = GetExactRecord(candidates, record.ServiceId, record.GrainId, record.Name);

            if (string.IsNullOrWhiteSpace(entry.ETag))
            {
                if (foundRecord is not null)
                {
                    record.ETag = foundRecord.ETag;
                    ctx.Reminders.Update(record);
                }
                else
                {
                    ctx.Reminders.Add(record);
                }
            }
            else
            {
                ctx.Reminders.Update(record);
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);

            return this._eTagConverter.FromDbETag(record.ETag);
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure to upsert reminder for service {ServiceId}", this._serviceId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var grainIdValue = grainId.ToString();
            var grainIdHash = EFCoreIdentifierHash.Compute(grainIdValue);
            var reminderNameHash = EFCoreIdentifierHash.Compute(reminderName);
            var candidates = await ctx.Reminders.Where(r =>
                    r.ServiceIdHash == this._serviceIdHash &&
                    r.GrainIdHash == grainIdHash &&
                    r.ReminderNameHash == reminderNameHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var record = GetExactRecord(candidates, this._serviceId, grainIdValue, reminderName);

            if (record is null) return false;

            ctx.Entry(record).Property(r => r.ETag).OriginalValue =
                this._eTagConverter.ToDbETag(eTag);
            ctx.Reminders.Remove(record);

            await ctx.SaveChangesAsync().ConfigureAwait(false);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (Exception exc)
        {
            _logger.LogError(
                exc,
                "Failure removing reminders for service {ServiceId} with GrainId {GrainId} and name {ReminderName}",
                this._serviceId,
                grainId,
                reminderName);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var records = await ctx.Reminders
                .Where(r => r.ServiceIdHash == this._serviceIdHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            EnsureExactService(records, this._serviceId);

            ctx.Reminders.RemoveRange(records);

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure to clear reminders for service {ServiceId}", this._serviceId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    private ReminderRecord<TETag> ConvertToRecord(ReminderEntry entry)
    {
        var record = new ReminderRecord<TETag>
        {
            ServiceIdHash = this._serviceIdHash,
            GrainIdHash = EFCoreIdentifierHash.Compute(entry.GrainId.ToString()),
            ReminderNameHash = EFCoreIdentifierHash.Compute(entry.ReminderName),
            ServiceId = this._serviceId,
            GrainHash = entry.GrainId.GetUniformHashCode(),
            GrainId = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            Period = entry.Period,
            StartAt = entry.StartAt
        };

        if (!string.IsNullOrWhiteSpace(entry.ETag))
        {
            record.ETag = this._eTagConverter.ToDbETag(entry.ETag);
        }

        return record;
    }

    private static ReminderRecord<TETag>? GetExactRecord(
        ReminderRecord<TETag>[] candidates,
        string serviceId,
        string grainId,
        string reminderName)
    {
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.ServiceId, serviceId, StringComparison.Ordinal) ||
                !string.Equals(candidate.GrainId, grainId, StringComparison.Ordinal) ||
                !string.Equals(candidate.Name, reminderName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An Entity Framework Core reminder identifier hash collision was detected.");
            }
        }

        return candidates.SingleOrDefault();
    }

    private static void EnsureExactGrain(ReminderRecord<TETag>[] records, string serviceId, string grainId)
    {
        if (records.Any(record =>
            !string.Equals(record.ServiceId, serviceId, StringComparison.Ordinal) ||
            !string.Equals(record.GrainId, grainId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("An Entity Framework Core reminder identifier hash collision was detected.");
        }
    }

    private static void EnsureExactService(ReminderRecord<TETag>[] records, string serviceId)
    {
        if (records.Any(record => !string.Equals(record.ServiceId, serviceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("An Entity Framework Core reminder identifier hash collision was detected.");
        }
    }

    private ReminderEntry ConvertToEntity(ReminderRecord<TETag> record)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Parse(record.GrainId),
            ReminderName = record.Name,
            Period = record.Period,
            StartAt = record.StartAt.UtcDateTime,
            ETag = this._eTagConverter.FromDbETag(record.ETag)
        };
    }
}
