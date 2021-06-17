using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.ReminderService
{
    internal class InMemoryReminderTable : IReminderTable, ILifecycleParticipant<ISiloLifecycle>
    {
        internal const long ReminderTableGrainId = 12345;
        private readonly IReminderTableGrain reminderTableGrain;
        private bool isAvailable;

        public InMemoryReminderTable(IGrainFactory grainFactory)
        {
            this.reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(ReminderTableGrainId);
        }

        public Task Init() => Task.CompletedTask;

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.ReadRow(grainRef, reminderName);
        }

        public Task<ReminderTableData> ReadRows(GrainReference key)
        {
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.ReadRows(key);
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            return this.isAvailable ? this.reminderTableGrain.ReadRows(begin, end) : Task.FromResult(new ReminderTableData());
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.RemoveRow(grainRef, reminderName, eTag);
        }

        public Task TestOnlyClearTable()
        {
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.TestOnlyClearTable();
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.UpsertRow(entry);
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
                nameof(InMemoryReminderTable),
                ServiceLifecycleStage.ApplicationServices,
                OnApplicationServicesStart,
                OnApplicationServicesStop);
        }
    }
}
