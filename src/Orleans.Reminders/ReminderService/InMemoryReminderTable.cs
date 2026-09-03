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
            this.reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(ReminderTableGrainId);
        }

        public Task Init() => Task.CompletedTask;

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
            => ReadRow(grainId, reminderName, CancellationToken.None);

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.ReadRow(grainId, reminderName, cancellationToken);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId)
            => ReadRows(grainId, CancellationToken.None);

        public Task<ReminderTableData> ReadRows(GrainId grainId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.ReadRows(grainId, cancellationToken);
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
            => ReadRows(begin, end, CancellationToken.None);

        public Task<ReminderTableData> ReadRows(uint begin, uint end, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return this.isAvailable ? this.reminderTableGrain.ReadRows(begin, end, cancellationToken) : Task.FromResult(new ReminderTableData());
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
            => RemoveRow(grainId, reminderName, eTag, CancellationToken.None);

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.RemoveRow(grainId, reminderName, eTag, cancellationToken);
        }

        public Task TestOnlyClearTable()
            => TestOnlyClearTable(CancellationToken.None);

        public Task TestOnlyClearTable(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.TestOnlyClearTable(cancellationToken);
        }

        public Task<string?> UpsertRow(ReminderEntry entry)
            => UpsertRow(entry, CancellationToken.None);

        public Task<string?> UpsertRow(ReminderEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfNotAvailable();
            return this.reminderTableGrain.UpsertRow(entry, cancellationToken);
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
