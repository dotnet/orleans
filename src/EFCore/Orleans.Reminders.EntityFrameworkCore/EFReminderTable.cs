using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders.EntityFrameworkCore;

public class EFReminderTable<TDbContext> : IReminderTable where TDbContext : ReminderDbContext<TDbContext>
{
    private readonly ILogger _logger;
    private readonly string _serviceId;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;

    public EFReminderTable(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IDbContextFactory<TDbContext> dbContextFactory)
    {
        this._logger = loggerFactory.CreateLogger<EFReminderTable<TDbContext>>();
        this._serviceId = clusterOptions.Value.ServiceId;
        this._dbContextFactory = dbContextFactory;
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
            var ctx = this._dbContextFactory.CreateDbContext();

            var records = await ctx.Reminders.AsNoTracking().Where(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainId == grainId.ToString())
                .ToArrayAsync().ConfigureAwait(false);

            return new ReminderTableData(records.Select(ConvertToEntity));
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
            var ctx = this._dbContextFactory.CreateDbContext();

            var query = ctx.Reminders.AsNoTracking()
                .Where(r => r.ServiceId == this._serviceId);

            query = begin < end
                ? query.Where(r => r.GrainHash > begin && r.GrainHash <= end)
                : query.Where(r => r.GrainHash > begin || r.GrainHash <= end);

            var records = await query.ToArrayAsync().ConfigureAwait(false);

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

    public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var record = await ctx.Reminders
                .AsNoTracking()
                .SingleOrDefaultAsync(r =>
                    r.ServiceId == this._serviceId &&
                    r.Name == reminderName &&
                    r.GrainId == grainId.ToString())
                .ConfigureAwait(false);

            return record is null ? null! : ConvertToEntity(record);
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure reading reminder {Name} for service {ServiceId} and grain {GrainId}", reminderName, this._serviceId, grainId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        try
        {
            var record = ConvertToRecord(entry);

            var ctx = this._dbContextFactory.CreateDbContext();

            ctx.Reminders.Update(record);

            await ctx.SaveChangesAsync().ConfigureAwait(false);

            return BitConverter.ToUInt64(record.ETag).ToString();
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
            var ctx = this._dbContextFactory.CreateDbContext();

            var record = await ctx.Reminders.SingleOrDefaultAsync(r =>
                    r.ServiceId == this._serviceId &&
                    r.GrainId == grainId.ToString() &&
                    r.Name == reminderName)
                .ConfigureAwait(false);

            if (record is null) return true;

            record.ETag = BitConverter.GetBytes(ulong.Parse(eTag));

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
            var ctx = this._dbContextFactory.CreateDbContext();

            var records = await ctx.Reminders
                .Where(r => r.ServiceId == this._serviceId)
                .ToArrayAsync()
                .ConfigureAwait(false);

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

    private ReminderRecord ConvertToRecord(ReminderEntry entry)
    {
        return new ReminderRecord
        {
            ServiceId = this._serviceId,
            GrainHash = entry.GrainId.GetUniformHashCode(),
            GrainId = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            Period = entry.Period,
            StartAt = entry.StartAt,
            ETag = BitConverter.GetBytes(ulong.Parse(entry.ETag))
        };
    }

    private ReminderEntry ConvertToEntity(ReminderRecord record)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Parse(record.GrainId),
            ReminderName = record.Name,
            Period = record.Period,
            StartAt = record.StartAt.UtcDateTime,
            ETag = BitConverter.ToUInt64(record.ETag).ToString()
        };
    }
}