using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.AdoNet.Storage;

namespace Orleans.Runtime.ReminderService
{
    internal sealed class AdoNetReminderTable : IReminderTable
    {
        private readonly AdoNetReminderTableOptions options;
        private readonly string serviceId;
        private RelationalOrleansQueries orleansQueries;

        public AdoNetReminderTable(
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AdoNetReminderTableOptions> storageOptions)
        {
            this.serviceId = clusterOptions.Value.ServiceId;
            this.options = storageOptions.Value;
        }

        public Task Init() => Init(CancellationToken.None);
        public Task<ReminderTableData> ReadRows(GrainId grainId) => ReadRows(grainId, CancellationToken.None);
        public Task<ReminderTableData> ReadRows(uint begin, uint end) => ReadRows(begin, end, CancellationToken.None);
        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName) => ReadRow(grainId, reminderName, CancellationToken.None); 
        public Task<string> UpsertRow(ReminderEntry entry) => UpsertRow(entry, CancellationToken.None); 
        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => RemoveRow(grainId, reminderName, eTag, CancellationToken.None);
        public Task TestOnlyClearTable() => TestOnlyClearTable(CancellationToken.None);

        public async Task Init(CancellationToken cancellationToken)
        {
            this.orleansQueries = await RelationalOrleansQueries.CreateInstance(this.options.Invariant, this.options.ConnectionString, cancellationToken);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId, CancellationToken cancellationToken)
        {
            return this.orleansQueries.ReadReminderRowsAsync(this.serviceId, grainId, cancellationToken);
        }

        public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash, CancellationToken cancellationToken)
        {
            return this.orleansQueries.ReadReminderRowsAsync(this.serviceId, beginHash, endHash, cancellationToken);
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName, CancellationToken cancellationToken)
        {
            return this.orleansQueries.ReadReminderRowAsync(this.serviceId, grainId, reminderName, cancellationToken);
        }

        public Task<string> UpsertRow(ReminderEntry entry, CancellationToken cancellationToken)
        {
            if (entry.StartAt.Kind is DateTimeKind.Unspecified)
            {
                entry.StartAt = new DateTime(entry.StartAt.Ticks, DateTimeKind.Utc);
            }

            return this.orleansQueries.UpsertReminderRowAsync(this.serviceId, entry.GrainId, entry.ReminderName, entry.StartAt, entry.Period, cancellationToken);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag, CancellationToken cancellationToken)
        {
            return this.orleansQueries.DeleteReminderRowAsync(this.serviceId, grainId, reminderName, eTag, cancellationToken);
        }

        public Task TestOnlyClearTable(CancellationToken cancellationToken)
        {
            return this.orleansQueries.DeleteReminderRowsAsync(this.serviceId, cancellationToken);
        }
    }
}
