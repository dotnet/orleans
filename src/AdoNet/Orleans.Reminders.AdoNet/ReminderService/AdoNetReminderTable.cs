using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.AdoNet.Storage;

namespace Orleans.Runtime.ReminderService
{
    internal class AdoNetReminderTable : IReminderTable
    {
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private readonly AdoNetReminderTableOptions options;
        private readonly string serviceId;
        private RelationalOrleansQueries orleansQueries;

        public AdoNetReminderTable(
            IGrainReferenceConverter grainReferenceConverter, 
            IOptions<ClusterOptions> clusterOptions, 
            IOptions<AdoNetReminderTableOptions> storageOptions)
        {
            this.grainReferenceConverter = grainReferenceConverter;
            this.serviceId = clusterOptions.Value.ServiceId;
            this.options = storageOptions.Value;
        }

        public async Task Init()
        {
            this.orleansQueries = await RelationalOrleansQueries.CreateInstance(this.options.Invariant, this.options.ConnectionString, this.grainReferenceConverter);
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
