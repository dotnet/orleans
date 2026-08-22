using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class InMemoryReminderTable : IReminderTable, ILifecycleParticipant<ISiloLifecycle>
{
    internal const long ReminderTableGrainId = 12345;
    private readonly IReminderTableGrain reminderTableGrain;
    private bool isAvailable;

    public InMemoryReminderTable(IGrainFactory grainFactory)
    {
        this.reminderTableGrain = grainFactory.GetGrain<IReminderTableGrain>(ReminderTableGrainId);
    }

    public Task Init() => StartAsync();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.isAvailable = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        this.isAvailable = false;
        return Task.CompletedTask;
    }

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        this.ThrowIfNotAvailable();
        return this.reminderTableGrain.ReadRow(grainId, reminderName);
    }

    public Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        this.ThrowIfNotAvailable();
        return this.reminderTableGrain.ReadRows(grainId);
    }

    public Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        return this.isAvailable ? this.reminderTableGrain.ReadRows(begin, end) : Task.FromResult(new ReminderTableData());
    }

    public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        return this.isAvailable
            ? this.reminderTableGrain.ReadRows(begin, end, maxRows, continuationToken)
            : Task.FromResult(new ReminderTableData());
    }

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        this.ThrowIfNotAvailable();
        return this.reminderTableGrain.RemoveRow(grainId, reminderName, eTag);
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
            return this.StartAsync(ct);
        }

        Task OnApplicationServicesStop(CancellationToken ct)
        {
            return this.StopAsync(ct);
        }

        lifecycle.Subscribe(
            nameof(InMemoryReminderTable),
            ServiceLifecycleStage.ApplicationServices,
            OnApplicationServicesStart,
            OnApplicationServicesStop);
    }
}
