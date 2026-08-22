using System;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.AdoNet;
using Orleans.Configuration;
using Orleans.Reminders.AdoNet.Storage;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class AdoNetReminderTable : IReminderTable
{
    private readonly AdoNetReminderTableOptions options;
    private readonly string serviceId;
    private RelationalOrleansQueries orleansQueries = default!;

    public AdoNetReminderTable(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<AdoNetReminderTableOptions> storageOptions)
    {
        this.serviceId = clusterOptions.Value.ServiceId;
        this.options = storageOptions.Value;
    }

    public Task Init() => StartAsync(CancellationToken.None);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        this.orleansQueries = await RelationalOrleansQueries.CreateInstance(
            this.options.Invariant,
            this.options.ConnectionString,
            cancellationToken);
    }

    public Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        return this.orleansQueries.ReadReminderRowsAsync(this.serviceId, grainId);
    }

    public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
    {
        return this.orleansQueries.ReadReminderRowsAsync(this.serviceId, beginHash, endHash);
    }

    public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash, int maxRows, string? continuationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        var cursor = AdoNetReminderContinuation.Parse(continuationToken);
        return orleansQueries.ReadReminderRowsAsync(
            serviceId,
            beginHash,
            endHash,
            maxRows,
            cursor is not null,
            cursor?.Hash ?? 0,
            cursor?.GrainId ?? string.Empty,
            cursor?.ReminderName ?? string.Empty);
    }

    internal static class AdoNetReminderContinuation
    {
        public static string Format(ReminderEntry entry)
            => string.Concat(
                entry.GrainId.GetUniformHashCode().ToString("X8", CultureInfo.InvariantCulture),
                ".",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.GrainId.ToString())),
                ".",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.ReminderName)));

        public static Cursor? Parse(string? continuationToken)
        {
            if (continuationToken is null)
            {
                return null;
            }

            try
            {
                var segments = continuationToken.Split('.');
                if (segments.Length != 3
                    || !uint.TryParse(segments[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
                {
                    throw new FormatException();
                }

                return new Cursor(
                    hash,
                    Encoding.UTF8.GetString(Convert.FromBase64String(segments[1])),
                    Encoding.UTF8.GetString(Convert.FromBase64String(segments[2])));
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("The continuation token is invalid.", nameof(continuationToken), exception);
            }
        }

        internal sealed record Cursor(uint Hash, string GrainId, string ReminderName);
    }

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        return await this.orleansQueries.ReadReminderRowAsync(this.serviceId, grainId, reminderName);
    }

    public Task<string> UpsertRow(ReminderEntry entry)
    {
        NormalizeUtcFields(entry);

        return this.orleansQueries.UpsertReminderRowAsync(
            this.serviceId,
            entry.GrainId,
            entry.ReminderName,
            entry.StartAt,
            entry.Period,
            entry.CronExpression,
            entry.CronTimeZoneId,
            entry.NextDueUtc,
            entry.LastFireUtc,
            entry.Priority,
            entry.Action,
            entry.ScheduleId,
            entry.JobId,
            entry.JobShardId,
            entry.ETag);
    }

    internal static void NormalizeUtcFields(ReminderEntry entry)
    {
        entry.StartAt = NormalizeUtcKind(entry.StartAt);
        entry.NextDueUtc = NormalizeUtcKind(entry.NextDueUtc);
        entry.LastFireUtc = NormalizeUtcKind(entry.LastFireUtc);
    }

    private static DateTime NormalizeUtcKind(DateTime value)
        => value.Kind is DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value;

    private static DateTime? NormalizeUtcKind(DateTime? value)
        => value.HasValue ? NormalizeUtcKind(value.Value) : null;

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        return this.orleansQueries.DeleteReminderRowAsync(this.serviceId, grainId, reminderName, eTag);
    }

    public Task TestOnlyClearTable()
    {
        return this.orleansQueries.DeleteReminderRowsAsync(this.serviceId);
    }
}
