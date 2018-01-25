using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Reminders.AdoNet.Storage;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal class SqlReminderTable : IReminderTable
    {
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private readonly AdoNetOptions adoNetOptions;
        private readonly StorageOptions storageOptions;
        private string serviceId;
        private RelationalOrleansQueries orleansQueries;

        public SqlReminderTable(IGrainReferenceConverter grainReferenceConverter, IOptions<SiloOptions> siloOptions, IOptions<AdoNetOptions> adoNetOptions, IOptions<StorageOptions> storageOptions)
        {
            this.grainReferenceConverter = grainReferenceConverter;
            this.serviceId = siloOptions.Value.ServiceId.ToString();
            this.adoNetOptions = adoNetOptions.Value;
            this.storageOptions = storageOptions.Value;
        }

        public async Task Init()
        {
            this.orleansQueries = await RelationalOrleansQueries.CreateInstance(this.adoNetOptions.InvariantForReminders, this.storageOptions.DataConnectionStringForReminders, this.grainReferenceConverter);
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
