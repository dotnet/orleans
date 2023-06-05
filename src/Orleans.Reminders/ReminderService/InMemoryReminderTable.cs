using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.ReminderService
{
    internal sealed class InMemoryReminderTable : IReminderTable, ILifecycleParticipant<ISiloLifecycle>
    {
        internal const long ReminderTableGrainId = 12345;
        private readonly IReminderTableGrain reminderTableGrain;
        private bool isAvailable;

        public InMemoryReminderTable(IGrainFactory grainFactory)
        {
            reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(ReminderTableGrainId);
        }

        public Task Init() => Task.CompletedTask;

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            ThrowIfNotAvailable();
            return reminderTableGrain.ReadRow(grainId, reminderName);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            ThrowIfNotAvailable();
            return reminderTableGrain.ReadRows(grainId);
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end) => isAvailable ? reminderTableGrain.ReadRows(begin, end) : Task.FromResult(new ReminderTableData());

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            ThrowIfNotAvailable();
            return reminderTableGrain.RemoveRow(grainId, reminderName, eTag);
        }

        public Task TestOnlyClearTable()
        {
            ThrowIfNotAvailable();
            return reminderTableGrain.TestOnlyClearTable();
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            ThrowIfNotAvailable();
            return reminderTableGrain.UpsertRow(entry);
        }

        private void ThrowIfNotAvailable()
        {
            if (!isAvailable) throw new InvalidOperationException("The reminder service is not currently available.");
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            Task OnApplicationServicesStart(CancellationToken ct)
            {
                isAvailable = true;
                return Task.CompletedTask;
            }

            Task OnApplicationServicesStop(CancellationToken ct)
            {
                isAvailable = false;
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
