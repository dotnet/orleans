using System;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.Runtime.ReminderService
{
    internal class InMemoryReminderTable : IReminderTable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IReminderTableGrain reminderTableGrain;
        private bool isAvailable;

        public InMemoryReminderTable(IGrainFactory grainFactory)
        {
            this.reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId);
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
            if (!this.isAvailable) return new ReminderTableData();

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
            if (!this.isAvailable) throw new InvalidOperationException("The reminder service is not currently available.");
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            Task OnApplicationServicesStart(CancellationToken ct)
            {
                this.isAvailable = true;
                return Task.CompletedTask;
            }

            Task OnApplicationServicesStop(CancellationToken ct)
            {
                this.isAvailable = false;
                return Task.CompletedTask;
            }

            lifecycle.Subscribe(
                nameof(GrainBasedReminderTable),
                ServiceLifecycleStage.ApplicationServices,
                OnApplicationServicesStart,
                OnApplicationServicesStop);
        }
    }
}
