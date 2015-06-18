using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.ReminderService
{
    internal class NotImplementedReminderTable:IReminderTable
    {
        private static readonly Task<ReminderTableData> emptyReminderTableDataTask = Task.FromResult(new ReminderTableData());
        private static readonly Task<ReminderEntry> emptyReminderEntryTask = Task.FromResult(new ReminderEntry());

        public Task Init(Guid serviceId, string deploymentId, string connectionString)
        {
            return TaskDone.Done;
        }

        public Task<ReminderTableData> ReadRows(GrainReference key)
        {
            return emptyReminderTableDataTask;
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            return emptyReminderTableDataTask;
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return emptyReminderEntryTask;
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            throw new NotImplementedException("Reminders are not supported when ReminderServiceProviderType=NotImplemented");
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            throw new NotImplementedException("Reminders are not supported when ReminderServiceProviderType=NotImplemented");
        }

        public Task TestOnlyClearTable()
        {
            return TaskDone.Done;
        }
    }
}
