using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.ReminderService
{
    internal class InMemoryReminderTable : IReminderTable
    {
        private readonly IReminderTableGrain reminderTableGrain;
        private readonly ISiloLifecycle siloLifecycle;

        public InMemoryReminderTable(IGrainFactory grainFactory, ISiloLifecycle siloLifecycle)
        {
            this.reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
            this.siloLifecycle = siloLifecycle;
        }

        private bool IsAvailable
        {
            get => this.siloLifecycle.HighestCompletedStage >= ServiceLifecycleStage.BecomeActive
                && this.siloLifecycle.LowestStoppedStage > ServiceLifecycleStage.EnableGrainCalls;
        }

        public Task Init() => Task.CompletedTask;

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            this.ThrowIfNotAvailable();
            return await this.reminderTableGrain.ReadRow(grainRef, reminderName);
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            this.ThrowIfNotAvailable();
            return await this.reminderTableGrain.ReadRows(key);
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            if (!this.IsAvailable) return new ReminderTableData();

            return await this.reminderTableGrain.ReadRows(begin, end);
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            this.ThrowIfNotAvailable();
            return await this.reminderTableGrain.RemoveRow(grainRef, reminderName, eTag);
        }

        public async Task TestOnlyClearTable()
        {
            this.ThrowIfNotAvailable();
            await this.reminderTableGrain.TestOnlyClearTable();
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            this.ThrowIfNotAvailable();
            return await this.reminderTableGrain.UpsertRow(entry);
        }

        private void ThrowIfNotAvailable()
        {
            if (!this.IsAvailable)
            {
                throw new InvalidOperationException("The reminder service is not currently available.");
            }
        }
    }
}
