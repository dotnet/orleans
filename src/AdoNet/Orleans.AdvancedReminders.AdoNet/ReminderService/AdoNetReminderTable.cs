using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.AdoNet;
using Orleans.Configuration;
using Orleans.Reminders.AdoNet.Storage;

namespace Orleans.AdvancedReminders.Runtime.ReminderService
{
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

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            return this.orleansQueries.ReadReminderRowAsync(this.serviceId, grainId, reminderName);
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
}
