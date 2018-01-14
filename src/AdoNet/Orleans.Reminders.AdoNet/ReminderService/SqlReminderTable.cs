using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Reminders.AdoNet.Storage;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal class SqlReminderTable : IReminderTable
    {
        private readonly IGrainReferenceConverter grainReferenceConverter;
        private string serviceId;
        private RelationalOrleansQueries orleansQueries;

        public SqlReminderTable(IGrainReferenceConverter grainReferenceConverter, IOptions<SiloOptions> siloOptions)
        {
            this.grainReferenceConverter = grainReferenceConverter;
            this.serviceId = siloOptions.Value.ServiceId.ToString();
        }

        public async Task Init(GlobalConfiguration config)
        {
            orleansQueries = await RelationalOrleansQueries.CreateInstance(config.AdoInvariantForReminders, config.DataConnectionStringForReminders, this.grainReferenceConverter);
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            return orleansQueries.ReadReminderRowsAsync(serviceId, grainRef);
        }

        public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
        {
            return orleansQueries.ReadReminderRowsAsync(serviceId, beginHash, endHash);
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return orleansQueries.ReadReminderRowAsync(serviceId, grainRef, reminderName);
        }   
        
        public Task<string> UpsertRow(ReminderEntry entry)
        {
            return orleansQueries.UpsertReminderRowAsync(serviceId, entry.GrainRef, entry.ReminderName, entry.StartAt, entry.Period);            
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            return orleansQueries.DeleteReminderRowAsync(serviceId, grainRef, reminderName, eTag);            
        }

        public Task TestOnlyClearTable()
        {
            return orleansQueries.DeleteReminderRowsAsync(serviceId);
        }
    }
}
