using System;
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
            serviceId = clusterOptions.Value.ServiceId;
            options = storageOptions.Value;
        }

        public async Task Init()
        {
            orleansQueries = await RelationalOrleansQueries.CreateInstance(options.Invariant, options.ConnectionString);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            return orleansQueries.ReadReminderRowsAsync(serviceId, grainId);
        }

        public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
        {
            return orleansQueries.ReadReminderRowsAsync(serviceId, beginHash, endHash);
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            return orleansQueries.ReadReminderRowAsync(serviceId, grainId, reminderName);
        }   
        
        public Task<string> UpsertRow(ReminderEntry entry)
        {
            if (entry.StartAt.Kind is DateTimeKind.Unspecified)
            {
                entry.StartAt = new DateTime(entry.StartAt.Ticks, DateTimeKind.Utc);
            }

            return orleansQueries.UpsertReminderRowAsync(serviceId, entry.GrainId, entry.ReminderName, entry.StartAt, entry.Period);            
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            return orleansQueries.DeleteReminderRowAsync(serviceId, grainId, reminderName, eTag);            
        }

        public Task TestOnlyClearTable()
        {
            return orleansQueries.DeleteReminderRowsAsync(serviceId);
        }
    }
}
