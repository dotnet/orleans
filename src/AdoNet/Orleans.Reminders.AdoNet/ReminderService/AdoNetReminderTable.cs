using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.AdoNet.Storage;

namespace Orleans.Runtime.ReminderService
{
    internal class AdoNetReminderTable : IReminderTable
    {
        private readonly AdoNetReminderTableOptions options;
        private readonly IServiceProvider serviceProvider;
        private readonly string serviceId;
        private RelationalOrleansQueries orleansQueries;

        public AdoNetReminderTable(
            IServiceProvider serviceProvider, 
            IOptions<ClusterOptions> clusterOptions, 
            IOptions<AdoNetReminderTableOptions> storageOptions)
        {
            this.serviceProvider = serviceProvider;
            this.serviceId = clusterOptions.Value.ServiceId;
            this.options = storageOptions.Value;
        }

        public async Task Init()
        {
            var grainReferenceConverter = serviceProvider.GetRequiredService<GrainReferenceKeyStringConverter>();
            this.orleansQueries = await RelationalOrleansQueries.CreateInstance(this.options.Invariant, this.options.ConnectionString, grainReferenceConverter);
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            return this.orleansQueries.ReadReminderRowsAsync(this.serviceId, grainRef);
        }

        public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
        {
            return this.orleansQueries.ReadReminderRowsAsync(this.serviceId, beginHash, endHash);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return this.orleansQueries.ReadReminderRowAsync(this.serviceId, grainRef, reminderName);
        }   
        
        public Task<string> UpsertRow(ReminderEntry entry)
        {
            if (entry.StartAt.Kind is DateTimeKind.Unspecified)
            {
                entry.StartAt = new DateTime(entry.StartAt.Ticks, DateTimeKind.Utc);
            }

            return this.orleansQueries.UpsertReminderRowAsync(this.serviceId, entry.GrainRef, entry.ReminderName, entry.StartAt, entry.Period);            
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            return this.orleansQueries.DeleteReminderRowAsync(this.serviceId, grainRef, reminderName, eTag);            
        }

        public Task TestOnlyClearTable()
        {
            return this.orleansQueries.DeleteReminderRowsAsync(this.serviceId);
        }
    }
}
