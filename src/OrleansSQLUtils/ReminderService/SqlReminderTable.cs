using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.SqlUtils;


namespace Orleans.Runtime.ReminderService
{
    internal class SqlReminderTable: IReminderTable
    {
        private string serviceId;
        private RelationalOrleansQueries orleansQueries;

        public async Task Init(GlobalConfiguration config, Logger logger)
        {
            serviceId = config.ServiceId.ToString();
            orleansQueries = await RelationalOrleansQueries.CreateInstance(config.AdoInvariantForReminders, config.DataConnectionStringForReminders);
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
