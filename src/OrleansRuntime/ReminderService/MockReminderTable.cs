using System;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal class MockReminderTable : IReminderTable
    {
        private const string MOCK_E_TAG = "MockETag";
        private readonly TimeSpan delay;

        public MockReminderTable(TimeSpan delay)
        {
            this.delay = delay;
        }

        public Task Init(GlobalConfiguration config, Logger logger)
        {
            return TaskDone.Done;
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            await Task.Delay(delay);
            return null;
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            await Task.Delay(delay);
            return null;
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            await Task.Delay(delay);
            return null;
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            await Task.Delay(delay);
            return MOCK_E_TAG;
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            await Task.Delay(delay);
            return true;
        }

        public Task TestOnlyClearTable()
        {
            return Task.Delay(delay);
        }
    }
}
